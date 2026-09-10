using Bobcat.Generated.EventModel;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace Bobcat.CritterStack.Tests;

/// <summary>
/// Issue #106 — the generator emits Event Modeling slice descriptors from Gherkin, and this
/// assembly's <c>Wallet.feature</c> is the proof: written only in shipped Critter Stack grammar,
/// it declares two slices with no hand-written builder anywhere.
/// </summary>
/// <remarks>
/// These run against the <em>generated</em> <c>BobcatEventModelSource</c>, so the test failing to
/// compile is itself the headline assertion: a renamed command type breaks the build at the
/// feature that names it.
/// </remarks>
public class EventModelDescriptorTests
{
    private static EventModelDescriptor describe() => BobcatEventModelSource.Describe();

    private static EventModelSliceDescriptor slice(string name)
        => describe().Slices.Single(s => s.Name == name);

    [Fact]
    public void the_model_is_named_for_the_assembly_and_carries_every_declared_slice()
    {
        var model = describe();
        model.Name.ShouldBe("Bobcat.CritterStack.Tests");
        model.Slices.Select(s => s.Name).ShouldBe(
            ["OpenWallet", "CreditWallet", "DebitWallet", "AuditWallet", "SweepWallets", "OwnerWallets",
             "Shipments"],
            ignoreOrder: true);
    }

    [Fact]
    public void a_slice_is_a_scenario_level_grouping_so_several_scenarios_fold_into_one()
    {
        // Wallet.feature tags five scenarios @slice:CreditWallet (two are #259's per-event twins),
        // WalletHttp.feature tags two more (the HTTP lane of the same behaviour, issue #210), and
        // WalletAuditSpecification tags a code-first eighth (issue #170). A slice is a vertical
        // behaviour, not a document — and not an authoring style or a transport either — so they
        // are one descriptor with eight specifications.
        slice("CreditWallet").Specifications.Count.ShouldBe(8);
        slice("CreditWallet").Specifications.Select(s => s.Identity)
            .ShouldContain("Wallet Audit/a code first credit");
        slice("CreditWallet").Specifications.Select(s => s.Identity)
            .ShouldContain("Wallet over HTTP/Crediting a wallet over HTTP emits the credited event");
        slice("OpenWallet").Specifications.Count.ShouldBe(1);
    }

    // ---- issue #170: the same declarations, authored in raw C# ------------------------------

    [Fact]
    public void a_code_first_specification_declares_a_slice_with_the_same_shape_gherkin_would()
    {
        var audit = slice("AuditWallet");

        audit.Domain.ShouldBe("Wallets");
        audit.Pattern.ShouldBe(SlicePattern.Command);

        // Roles from the typed steps the scenario body names, in their slots — never one bag.
        audit.AggregateTypes.Select(t => t.Name).ShouldBe(["Wallet"]);
        audit.EmittedEvents.Select(t => t.Name).ShouldBe(["WalletDebited"]);
        audit.ReadModelTypes.Select(t => t.Name).ShouldBe(["WalletSummary"]);
        audit.PublishedMessages.Select(t => t.Name).ShouldBe(["WalletCreditedNotification"]);
    }

    [Fact]
    public void the_code_first_act_command_is_the_last_when_not_the_first()
    {
        // The scenario arranges with WhenCommand(OpenWallet) before the WhenCommand(DebitWallet)
        // it is about — the same last-When rule the Gherkin path applies.
        slice("AuditWallet").CommandType!.Name.ShouldBe("DebitWallet");
    }

    [Fact]
    public void the_code_first_identity_is_the_derived_feature_and_scenario_titles()
    {
        // WalletAuditSpecification → "Wallet Audit"; auditing_a_credited_wallet → underscores as
        // spaces. The exact string SpecificationFeature derives at runtime and publishes on
        // scenario_finished, so run evidence joins with no mapping table — pinned against the
        // runtime by CodeFirstNamingAgreementTests.
        slice("AuditWallet").Specifications.Select(s => s.Identity)
            .ShouldContain("Wallet Audit/auditing a credited wallet");
    }

    [Fact]
    public void an_empty_code_first_scenario_is_a_pending_specification_hotspot()
    {
        var pending = slice("AuditWallet").Hotspots.ShouldHaveSingleItem();
        pending.Origin.ShouldBe(HotspotOrigin.PendingSpecification);
        pending.SpecificationIdentity.ShouldBe("Wallet Audit/auditing an empty wallet");
    }

    [Fact]
    public void an_arrange_command_still_reaches_the_code_first_specification_as_evidence()
    {
        var spec = slice("AuditWallet").Specifications
            .Single(s => s.Identity == "Wallet Audit/auditing a credited wallet");
        spec.ResolvedTypes.Select(t => t.Name).ShouldContain("OpenWallet");
        spec.ResolvedTypes.Select(t => t.Name).ShouldContain("DebitWallet");
    }

    [Fact]
    public void the_domain_comes_from_the_feature_tag_and_is_inherited_by_every_scenario()
    {
        // @domain:Wallets sits on the Feature line; standard Gherkin inheritance carries it down.
        slice("OpenWallet").Domain.ShouldBe("Wallets");
        slice("CreditWallet").Domain.ShouldBe("Wallets");
    }

    [Fact]
    public void the_trigger_label_comes_from_the_triggered_by_description_line()
    {
        slice("OpenWallet").TriggerLabel.ShouldBe("the wallet holder");
    }

    [Fact]
    public void a_scenario_level_triggered_by_line_wins_over_the_features_for_its_slice()
    {
        // Issue #258: a slice is scenario-level, and a feature-level line stamped on every slice
        // gave nine of the CritterCrush chapter's ten slices a trigger belonging to another.
        slice("DebitWallet").TriggerLabel.ShouldBe("a card payment at the till");

        // The feature's line still reaches the slices whose scenarios declare none.
        slice("CreditWallet").TriggerLabel.ShouldBe("the wallet holder");
    }

    [Fact]
    public void the_command_is_the_act_not_the_first_command_the_scenario_names()
    {
        // The CreditWallet scenarios arrange by issuing `When OpenWallet is received` first. The
        // last When is the act; taking the first labelled this slice with OpenWallet.
        slice("CreditWallet").CommandType!.Name.ShouldBe("CreditWallet");
        slice("OpenWallet").CommandType!.Name.ShouldBe("OpenWallet");
    }

    [Fact]
    public void an_arrange_command_still_reaches_the_specification_as_evidence()
    {
        // It is not the slice's command, but the spec did touch the type, and run evidence
        // (#107) joins on exactly this list.
        var spec = slice("CreditWallet").Specifications
            .Single(s => s.Identity.EndsWith("emits the credited event and sends a notification"));
        spec.ResolvedTypes.Select(t => t.Name).ShouldContain("OpenWallet");
        spec.ResolvedTypes.Select(t => t.Name).ShouldContain("CreditWallet");
    }

    [Fact]
    public void roles_land_in_their_own_slots_rather_than_one_bag_of_types()
    {
        var credit = slice("CreditWallet");
        credit.EmittedEvents.Select(t => t.Name).ShouldBe(["WalletCredited"]);
        credit.AggregateTypes.Select(t => t.Name).ShouldBe(["Wallet"]);
        credit.ReadModelTypes.Select(t => t.Name).ShouldBe(["WalletSummary"]);
        // A published message is kept apart from the events so the event lane shows only events.
        credit.PublishedMessages.Select(t => t.Name).ShouldBe(["WalletCreditedNotification"]);
    }

    [Fact]
    public void the_spec_identity_is_the_same_string_the_runner_uses_as_a_test_id()
    {
        // {Feature}/{Scenario} — the same key SpecNodeMapping.Uid produces and the same one
        // scenario_finished carries, so design-time and run evidence join without a mapping table.
        slice("OpenWallet").Specifications.Single().Identity
            .ShouldBe("Wallet/Opening a wallet emits the opened event and starts an empty balance");
    }

    [Fact]
    public void the_pattern_is_derived_where_gherkin_can_tell_and_left_null_where_it_cannot()
    {
        // A slice that receives a command is a Command slice. Automation and Translation need a
        // trigger Gherkin does not express, so they are never guessed.
        slice("CreditWallet").Pattern.ShouldBe(SlicePattern.Command);
    }

    [Fact]
    public void the_rendering_contract_is_computed_upstream_from_the_roles_we_stamped()
    {
        // Elements/Edges are computed by JasperFx on read, which is why the generator stamps only
        // roles — building a graph here would be a second opinion about the same slice.
        var elements = slice("CreditWallet").Elements;
        elements.ShouldNotBeEmpty();
        elements.Select(e => e.Kind).ShouldContain(EventModelElementKind.Command);
        elements.Select(e => e.Kind).ShouldContain(EventModelElementKind.Event);
        elements.Where(e => e.Kind == EventModelElementKind.Event)
            .Select(e => e.Lane).ShouldAllBe(lane => lane == EventModelLane.EventStream);
    }

    [Fact]
    public async Task the_source_surfaces_the_descriptor_through_the_jasperfx_interface()
    {
        // The acceptance criterion: a host reports its slices through IEventModelDefinitionSource
        // with no hand-written builder.
        IEventModelDefinitionSource source = BobcatEventModelSource.Instance;
        source.Subject.ShouldBe(new Uri("event-model://Bobcat.CritterStack.Tests"));

        var descriptor = await source.TryCreateAsync(null!, TestContext.Current.CancellationToken);
        descriptor.ShouldNotBeNull();
        descriptor.Slices.Count.ShouldBe(7);
    }

    [Fact]
    public void a_document_capture_stamps_no_event_modeling_role()
    {
        // Shipments.feature is the document lane (issue #270), against an application with no
        // event store. It reaches the model at all because it genuinely declares Event Modeling
        // elements — a command is posted, a message is sent — and untagged scenarios with roles
        // fall back to the feature title.
        var shipments = slice("Shipments");

        shipments.CommandType!.Name.ShouldBe(nameof(ShipmentRequest));

        // …but {document} is NOT one of them. A document-backed application has no stream, and
        // stamping an aggregate, event or read model for it would put elements on the canvas that
        // describe nothing. Inert by construction: EventModelEmitter switches on the role words
        // and lets {document} fall through, exactly as it does {type}.
        shipments.AggregateTypes.ShouldBeEmpty();
        shipments.EmittedEvents.ShouldBeEmpty();
        shipments.ReadModelTypes.ShouldBeEmpty();
    }

    [Fact]
    public void an_arranged_event_is_resolved_but_never_stamped_as_one_the_slice_emits()
    {
        // "A wallet with prior events keeps accumulating, arranged per event" lays down
        // WalletOpened and WalletCredited with `Given {event} occurred` (issue #259), then acts.
        var credit = slice("CreditWallet");

        // WalletOpened is prior history. The slice does not emit it, and stamping it would draw an
        // arrow on the canvas for an output CreditWallet never writes — worse than a missing one.
        credit.EmittedEvents.Select(t => t.Name).ShouldNotContain("WalletOpened");

        // …but it is still a type the specification resolved, which is where "this spec touched
        // that type" belongs and what #107's run evidence joins on — the same treatment arrange
        // COMMANDS already get.
        var perEvent = credit.Specifications
            .Single(s => s.Identity == "Wallet/A wallet with prior events keeps accumulating, arranged per event");
        perEvent.ResolvedTypes.Select(t => t.Name).ShouldContain("WalletOpened");
    }

    [Fact]
    public void the_event_the_slice_really_does_emit_is_still_stamped()
    {
        // The counterweight: the demotion is keyed on the GIVEN keyword, not on the type. The same
        // WalletCredited that is arranged in one scenario is emitted in others, and a Then must
        // still stamp it.
        slice("CreditWallet").EmittedEvents.Select(t => t.Name).ShouldContain("WalletCredited");
    }
}
