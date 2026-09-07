using Bobcat.EventModel;
using Bobcat.EventModel.Scaffolding;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

public class SliceScaffolderTests
{
    private static CuratedModelFile parse(string yaml)
    {
        var reading = CuratedModelReader.Read(yaml);
        reading.Problems.ShouldBeEmpty();
        return reading.File!;
    }

    internal const string ModelYaml =
        """
        schema: 1
        model: CritterCrush
        namespace: CritterCrush
        slices:
          - name: SwipeOnDog
            pattern: Command
            domain: Discovery
            trigger: { kind: Http, label: Discovery feed }
            command: SwipeOnDog
            aggregates: [SwipePair]
            events: [DogLiked, DogPassed]
            elements:
              DogLiked:
                description: A dog owner liked another dog
                fields: { swiperDogId: Guid, likedAt: DateTimeOffset }
            specifications:
              feature: Swiping
              scenarios:
                - name: A like is recorded
                  when: { command: SwipeOnDog, with: { swiperDogId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", liked: "true" } }
                  then: [{ event: DogLiked }]
                - name: Swiping a removed profile is refused
                  when: { command: SwipeOnDog }
                  then: [{ validationFails: "profile no longer available" }]
          - name: DetectMutualMatch
            pattern: Automation
            domain: Discovery
            trigger: { kind: MessageHandler, label: Dog Liked }
            aggregates: [SwipePair]
            events: [MutualMatchDetected]
            hotspots: ["Notify both owners?"]
            specifications:
              feature: Swiping
              scenarios:
                - name: A mutual like produces a match
                  then: [{ event: MutualMatchDetected }]
          - name: MatchList
            pattern: View
            domain: Discovery
            projections: [MatchListProjection]
            fanOut: true
            readModels: [MatchList]
          - name: ViewSwipePair
            pattern: View
            domain: Discovery
            readModels: [SwipePair]
        """;

    private static string scaffold(string sliceName)
    {
        var model = parse(ModelYaml);
        var slice = model.Slices.Single(x => x.Name == sliceName);
        return SliceScaffolder.Scaffold(model, slice).Single().Value;
    }

    [Fact]
    public void an_http_command_slice_collapses_the_endpoint_is_the_handler()
    {
        var code = scaffold("SwipeOnDog");

        // The collapsed default (CritterStackSamples#13): one transaction, honest status codes.
        code.ShouldContain("public record DogLiked(Guid SwiperDogId, DateTimeOffset LikedAt");
        code.ShouldContain("public record SwipeOnDogRequest(Guid SwiperDogId, bool Liked);");
        code.ShouldContain("public record SwipeOnDogResponse();");
        code.ShouldContain("[WolverinePost(\"/api/discovery/swipeondog\")]");
        code.ShouldContain("public static (SwipeOnDogResponse, EventsToAppend) Post(SwipeOnDogRequest request, [WriteModel] SwipePair? swipePair)");
        // The aggregate is NOT here — it is a model-level artifact now (see below).
        code.ShouldNotContain("public class SwipePair");
        code.ShouldContain("wolverine#4309");
        // No two-hop shape: the bus-visible command handler is opt-in, not the default.
        code.ShouldNotContain("SwipeOnDogHandler");
    }

    [Fact]
    public void refusals_are_harvested_into_the_validate_railway_stub()
    {
        var code = scaffold("SwipeOnDog");

        code.ShouldContain("public static ProblemDetails Validate(SwipeOnDogRequest request)");
        code.ShouldContain("""Detail = "profile no longer available", Status = 400""");
        code.ShouldContain("return WolverineContinue.NoProblems;");
    }

    [Fact]
    public void an_automation_slice_is_triggered_by_its_event_and_never_gets_a_route()
    {
        var code = scaffold("DetectMutualMatch");

        code.ShouldContain("public static EventsToAppend Handle(DogLiked trigger, [WriteModel] SwipePair swipePair)");
        code.ShouldContain("HOTSPOT (from the model): Notify both owners?");
        code.ShouldNotContain("WolverinePost");
    }

    [Fact]
    public void a_fan_out_view_slice_gets_a_multi_stream_projection_and_a_document_load()
    {
        var code = scaffold("MatchList");

        code.ShouldContain("public class MatchList");
        code.ShouldContain("public class MatchListProjection : MultiStreamProjection<MatchList, Guid>");
        code.ShouldContain("Identities<SourceEvent>");
        code.ShouldContain("daemon RUNNING");
        code.ShouldContain("session.LoadAsync<MatchList>(id, ct)");
        // A fan-out is not a single-stream aggregation — [ReadAggregate] can never serve it.
        code.ShouldNotContain("[ReadAggregate]");
    }

    [Fact]
    public void a_projector_less_view_slice_reads_the_snapshot_with_read_aggregate_and_emits_no_duplicate_class()
    {
        var code = scaffold("ViewSwipePair");

        // The write model IS the read model: [ReadAggregate] (single-stream aggregations only),
        // no second SwipePair class, no session ceremony.
        code.ShouldContain("public static SwipePair Get([ReadAggregate] SwipePair swipePair) => swipePair;");
        code.ShouldNotContain("public class SwipePair");
        code.ShouldNotContain("LoadAsync");
    }

    [Fact]
    public void the_aggregate_is_emitted_once_for_the_whole_model_not_once_per_slice()
    {
        // Two slices declare SwipePair; nine would too. Emitting it per slice produces partial
        // duplicates of one type that cannot compile — an aggregate is a model-level concern,
        // exactly like a feature file.
        var files = SliceScaffolder.ScaffoldAggregates(parse(ModelYaml));

        var (path, code) = files.Single();
        path.ShouldBe("Discovery/SwipePair.cs");
        code.ShouldContain("public class SwipePair");
        code.ShouldContain("never DateTimeOffset.UtcNow");

        // ...and it folds EVERY declaring slice's events, not just the first slice's.
        code.ShouldContain("public static SwipePair Create(DogLiked dogLiked)");
        code.ShouldContain("public void Apply(DogPassed dogPassed)");
        code.ShouldContain("public void Apply(MutualMatchDetected mutualMatchDetected)");
    }

    [Fact]
    public void every_scenario_gets_its_own_stream_id_because_scenarios_share_a_store()
    {
        var swiping = SliceScaffolder.ScaffoldFeatures(parse(ModelYaml)).Single().Value;

        var ids = swiping.Split('\n')
            .Where(x => x.Contains("Given no events for "))
            .Select(x => x.Split('"')[1])
            .ToList();

        ids.Count.ShouldBeGreaterThan(1);
        ids.Distinct().Count().ShouldBe(ids.Count);
    }

    [Fact]
    public void features_merge_across_slices_because_slices_legally_share_one()
    {
        // Per-slice feature emission clobbered scenarios on the CritterCrush corpus — feature
        // files group by the identity's feature half, model-wide.
        var features = SliceScaffolder.ScaffoldFeatures(parse(ModelYaml));

        var swiping = features.Single().Value;
        features.Single().Key.ShouldBe("Features/Swiping.feature");
        swiping.ShouldContain("@slice:SwipeOnDog");
        swiping.ShouldContain("@slice:DetectMutualMatch");
        swiping.ShouldContain("Scenario: A like is recorded");
        swiping.ShouldContain("Scenario: A mutual like produces a match");
    }

    [Fact]
    public void the_feature_reproduces_identities_and_grammar_exactly()
    {
        var swiping = SliceScaffolder.ScaffoldFeatures(parse(ModelYaml)).Single().Value;

        // ⚠️ THE load-bearing assertion of this type: Feature + Scenario are the identity that
        // joins the descriptor binding, Bobcat run evidence, and a Stoat spec-identity gate.
        swiping.ShouldContain("Feature: Swiping");
        swiping.ShouldContain("Then DogLiked is emitted");
        swiping.ShouldContain("And no events are emitted");

        // SwipeOnDog is HTTP-triggered, so its code is the collapsed endpoint and its act is the
        // POST that binds to it — never the bus dispatch, whose type that shape does not emit
        // (issue #231). Its refusal is the endpoint's 400 for the same reason.
        swiping.ShouldContain("When SwipeOnDogRequest is posted to \"/api/discovery/swipeondog\"");
        swiping.ShouldContain("# refused with: \"profile no longer available\"");
        swiping.ShouldContain("Then the response is 400");
    }

    [Fact]
    public void field_types_are_inferred_from_sample_values_when_not_named()
    {
        var code = scaffold("SwipeOnDog");

        // swiperDogId came typed from the element hint; liked only ever appears as the sample
        // value "true" in a scenario column, and infers to bool.
        code.ShouldContain("bool Liked");
    }
}
