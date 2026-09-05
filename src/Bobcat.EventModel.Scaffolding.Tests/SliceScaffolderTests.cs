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

    private const string Model =
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
            readModels: [MatchList]
        """;

    private static string scaffold(string sliceName)
    {
        var model = parse(Model);
        var slice = model.Slices.Single(x => x.Name == sliceName);
        return SliceScaffolder.Scaffold(model, slice).Single().Value;
    }

    [Fact]
    public void a_command_slice_scaffolds_the_aggregate_workflow_shape()
    {
        var code = scaffold("SwipeOnDog");

        // The mechanical 80%: shapes, attributes, and warnings — with judgment as marked TODOs.
        code.ShouldContain("public record DogLiked(Guid SwiperDogId, DateTimeOffset LikedAt");
        code.ShouldContain("public record SwipeOnDog(Guid SwiperDogId, bool Liked);");
        code.ShouldContain("public static class SwipeOnDogHandler");
        code.ShouldContain("public static EventsToAppend Handle(SwipeOnDog command, [WriteModel] SwipePair? swipePair)");
        code.ShouldContain("public static SwipePair Create(DogLiked dogLiked)");
        code.ShouldContain("public void Apply(DogPassed dogPassed)");
        code.ShouldContain("never DateTimeOffset.UtcNow");
        code.ShouldContain("wolverine#4309");
    }

    [Fact]
    public void guards_are_harvested_from_the_scenarios_refusals()
    {
        scaffold("SwipeOnDog")
            .ShouldContain("""throw new InvalidOperationException("profile no longer available")""");
    }

    [Fact]
    public void a_command_slice_with_an_http_trigger_gets_the_pure_translation_endpoint()
    {
        var code = scaffold("SwipeOnDog");

        code.ShouldContain("[WolverinePost(\"/api/discovery/swipeondog\")]");
        code.ShouldContain("public static (CreationResponse, SwipeOnDog) Post(SwipeOnDogRequest request)");
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
    public void a_view_slice_scaffolds_read_model_projection_and_get()
    {
        var code = scaffold("MatchList");

        code.ShouldContain("public class MatchList");
        code.ShouldContain("public class MatchListProjection : SingleStreamProjection<MatchList, Guid>");
        code.ShouldContain("daemon RUNNING");
        code.ShouldContain("[WolverineGet(\"/api/matchlist/{id}\")]");
    }

    [Fact]
    public void features_merge_across_slices_because_slices_legally_share_one()
    {
        // Per-slice feature emission clobbered scenarios on the CritterCrush corpus — feature
        // files group by the identity's feature half, model-wide.
        var features = SliceScaffolder.ScaffoldFeatures(parse(Model));

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
        var swiping = SliceScaffolder.ScaffoldFeatures(parse(Model)).Single().Value;

        // ⚠️ THE load-bearing assertion of this type: Feature + Scenario are the identity that
        // joins the descriptor binding, Bobcat run evidence, and a Stoat spec-identity gate.
        swiping.ShouldContain("Feature: Swiping");
        swiping.ShouldContain("When SwipeOnDog is received");
        swiping.ShouldContain("Then DogLiked is emitted");
        swiping.ShouldContain("Then validation fails with \"profile no longer available\"");
        swiping.ShouldContain("And no events are emitted");
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
