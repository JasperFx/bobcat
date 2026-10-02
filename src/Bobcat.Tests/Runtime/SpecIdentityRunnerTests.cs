using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// Issue #391, the Gherkin lane in process: listing what a runner specifies, and narrowing a run
/// to identities a monitor named.
/// </summary>
public class SpecIdentityRunnerTests
{
    public class IdentityFixture : Fixture;

    private static readonly List<string> ran = new();

    public SpecIdentityRunnerTests() => ran.Clear();

    private static FeatureDefinition feature(string title, params string[] scenarios)
        => new(title, typeof(IdentityFixture),
            scenarios.Select(s => new ScenarioDefinition(s, [], (_, plan) =>
                plan.Add(new DelegateExecutionStep("step", StepKind.Then, s, (_, result, _) =>
                {
                    ran.Add(SpecIdentity.Of(title, s));
                    result.MarkSuccess();
                    return Task.CompletedTask;
                })))).ToArray());

    private static BobcatRunner runner()
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(feature("Orders", "places an order", "cancels an order"));
        runner.AddFeature(feature("Stock", "counts"));
        return runner;
    }

    [Fact]
    public void a_runner_lists_every_identity_it_discovered()
    {
        runner().SpecIdentities.ShouldBe([
            "Orders/places an order",
            "Orders/cancels an order",
            "Stock/counts"
        ]);
    }

    [Fact]
    public void the_manifest_says_which_lane_and_framework_owns_the_process()
    {
        // Only the runner knows its lane, which is the premise of the whole issue — so the
        // manifest states it rather than leaving a reader to infer it.
        var manifest = runner().Manifest();

        manifest.Lane.ShouldBe("gherkin");
        manifest.Framework.ShouldBe("bobcat");
        manifest.Identities.ShouldBe([
            "Orders/cancels an order",
            "Orders/places an order",
            "Stock/counts"
        ], "sorted, because this is a set a runner registers and checks membership against");
    }

    [Fact]
    public async Task narrowing_to_identities_runs_exactly_those_scenarios()
    {
        var subject = runner();
        subject.NarrowTo(SpecSelection.Of("Orders/cancels an order", "Stock/counts"));

        await subject.RunAll();

        ran.ShouldBe(["Orders/cancels an order", "Stock/counts"]);
    }

    [Fact]
    public async Task narrowing_to_nothing_in_particular_runs_the_whole_suite()
    {
        var subject = runner();
        subject.NarrowTo(SpecSelection.Everything);

        await subject.RunAll();

        ran.Count.ShouldBe(3);
    }

    [Fact]
    public async Task a_selection_narrows_the_platform_filter_and_never_widens_it()
    {
        // An MTP host has already set ScenarioFilter from the platform's uid filter by the time a
        // request arrives. A selection that replaced it would run tests the platform did not ask
        // for, so the two compose as AND.
        var subject = runner();
        subject.ScenarioFilter = (f, _) => f.Title == "Orders";
        subject.NarrowTo(SpecSelection.Of("Orders/places an order", "Stock/counts"));

        await subject.RunAll();

        ran.ShouldBe(["Orders/places an order"]);
    }
}
