using Bobcat.EventModel;
using Bobcat.EventModel.Scaffolding;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// The issue #218 acceptance fixture: no <c>busVisible:</c> flag anywhere — the model itself
/// designates bus visibility by the cross-slice join between one slice's <c>messages:</c> and
/// another slice's <c>command:</c>, gated on the handler taking it off the bus (non-HTTP
/// trigger). The worked inter-module example lives in CritterStackSamples#15; this curated
/// model is the inference's contract.
/// </summary>
public class BusVisibilityTests
{
    private static CuratedModelFile parse(string yaml)
    {
        var reading = CuratedModelReader.Read(yaml);
        reading.Problems.ShouldBeEmpty();
        return reading.File!;
    }

    private const string Model =
        """
        schema: 1
        model: Marketplace
        namespace: Marketplace
        slices:
          # A publishes, B handles: the join that makes ReviewListing bus-visible.
          - name: RemoveListing
            pattern: Command
            domain: Listings
            trigger: { kind: Http }
            command: RemoveListing
            aggregates: [Listing]
            events: [ListingRemoved]
            messages: [ReviewListing]
          - name: ReviewListing
            pattern: Command
            domain: Moderation
            trigger: { kind: MessageHandler }
            command: ReviewListing
            aggregates: [Review]
            events: [ListingReviewed]
            elements:
              ReviewListing:
                fields: { listingId: Guid, reason: string }
          # The modeling gap: published, but nothing handles it and nothing marks it leaving.
          - name: ArchiveListing
            pattern: Command
            domain: Listings
            trigger: { kind: Http }
            command: ArchiveListing
            events: [ListingArchived]
            messages: [PurgeListingMedia]
          # An HTTP-triggered match is NOT a bus handler: routes are not queues.
          - name: ReportSeller
            pattern: Command
            domain: Listings
            trigger: { kind: Http }
            command: ReportSeller
            events: [SellerReported]
            messages: [SuspendSeller]
          - name: SuspendSeller
            pattern: Command
            domain: Moderation
            trigger: { kind: Http }
            command: SuspendSeller
            events: [SellerSuspended]
          # An outbound external edge marks the unmatched message as leaving the system.
          - name: SyncSearchIndex
            pattern: Automation
            domain: Listings
            trigger: { kind: MessageHandler, label: Listing Removed }
            aggregates: [Listing]
            events: [ListingRemoved]
            messages: [ListingIndexDropV1]
            externalSystems:
              - { name: SearchIndex, direction: Outbound }
          # Eventless HTTP slice publishing one handled command: the model selects the two-hop shape.
          - name: SubmitModerationCase
            pattern: Command
            domain: Moderation
            trigger: { kind: Http }
            command: SubmitModerationCase
            messages: [ReviewListing]
        """;

    private static string scaffold(string sliceName)
    {
        var model = parse(Model);
        var slice = model.Slices.Single(x => x.Name == sliceName);
        return SliceScaffolder.Scaffold(model, slice).Single().Value;
    }

    [Fact]
    public void a_published_command_another_slice_handles_off_the_bus_is_cascaded_in_the_tuple()
    {
        var code = scaffold("RemoveListing");

        // The model selected the cascading shape — no busVisible flag, no by-hand opt-in.
        code.ShouldContain("public static (RemoveListingResponse, EventsToAppend, ReviewListing) Post(RemoveListingRequest request, [WriteModel] Listing? listing)");
        code.ShouldContain("slice 'ReviewListing' handles it; the cascade rides the transactional outbox");
        code.ShouldContain("return (new RemoveListingResponse(/* TODO */), [new ListingRemoved(/* TODO */)], new ReviewListing(/* TODO */));");
        // The handling slice owns the command record; the publisher never re-declares it.
        code.ShouldNotContain("public record ReviewListing(");
        code.ShouldNotContain("WARNING");
    }

    [Fact]
    public void the_handling_slice_keeps_the_message_handler_shape_and_names_its_publisher()
    {
        var code = scaffold("ReviewListing");

        // Its trigger IS the published command: WriteModelHandlerFrame, never a route.
        code.ShouldContain("public record ReviewListing(Guid ListingId, string Reason);");
        code.ShouldContain("public static EventsToAppend Handle(ReviewListing command, [WriteModel] Review? review)");
        code.ShouldContain("Triggered over the bus: the model shows slice 'RemoveListing' publishing ReviewListing.");
        code.ShouldNotContain("WolverinePost");
    }

    [Fact]
    public void a_published_command_nothing_handles_is_a_scaffold_time_warning_not_a_cascade()
    {
        var code = scaffold("ArchiveListing");

        // Design point 1: an unhandled published command is usually a modeling gap — warn, and
        // keep the collapsed shape untouched rather than cascading into the void.
        code.ShouldContain("// WARNING (from the model): slice 'ArchiveListing' publishes 'PurgeListingMedia', but no slice declares it as its command");
        code.ShouldContain("public static (ArchiveListingResponse, EventsToAppend) Post(");
        code.ShouldNotContain("new PurgeListingMedia(");

        BusVisibility.Warnings(parse(Model)).ShouldContain(x => x.Contains("PurgeListingMedia"));
    }

    [Fact]
    public void an_http_triggered_match_does_not_make_the_command_bus_visible()
    {
        var code = scaffold("ReportSeller");

        // SuspendSeller exists — but it takes its command at a route. Published, it would go
        // unhandled: the inference requires a non-HTTP trigger on the handling slice.
        code.ShouldContain("// WARNING (from the model): slice 'ReportSeller' publishes 'SuspendSeller', but slice 'SuspendSeller' takes it at a route (Http trigger), not off the bus");
        code.ShouldContain("public static (ReportSellerResponse, EventsToAppend) Post(");
        code.ShouldNotContain("new SuspendSeller(");
    }

    [Fact]
    public void an_outbound_external_edge_marks_an_integration_message_leaving_the_system()
    {
        var code = scaffold("SyncSearchIndex");

        // Design point 1's refinement, already expressible: the outbound `externalSystems:` edge
        // says the message leaves the system, so no warning — and the publisher owns the record,
        // because no slice in this model declares its shape.
        code.ShouldContain("public record ListingIndexDropV1(");
        code.ShouldContain("public static (EventsToAppend, ListingIndexDropV1) Handle(");
        code.ShouldContain("ListingIndexDropV1 leaves the system (outbound external edge)");
        code.ShouldNotContain("WARNING");
    }

    [Fact]
    public void an_eventless_slice_publishing_one_handled_command_selects_the_two_hop_translation()
    {
        var code = scaffold("SubmitModerationCase");

        // The opt-in EndpointTranslationFrame, selected by the model rather than by hand: the
        // slice appends nothing itself, so the endpoint only mints identity and cascades.
        code.ShouldContain("public static (CreationResponse, ReviewListing) Post(SubmitModerationCaseRequest request)");
        code.ShouldContain("var command = new ReviewListing(/* TODO from request */);");
        code.ShouldNotContain("EventsToAppend");
        code.ShouldNotContain("SubmitModerationCaseResponse");
    }

    [Fact]
    public void a_model_with_every_published_message_accounted_for_raises_no_warnings()
    {
        var model = parse(Model);

        // The two deliberate gaps above are the only warnings the whole model produces.
        var warnings = BusVisibility.Warnings(model);
        warnings.Count.ShouldBe(2);
        warnings.ShouldContain(x => x.Contains("PurgeListingMedia"));
        warnings.ShouldContain(x => x.Contains("SuspendSeller"));
    }
}
