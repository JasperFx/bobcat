using Bobcat;
using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

public class EmptyRunFixture : Fixture;

/// <summary>
/// Issue #273: a run that has nothing to execute fails instead of passing.
/// </summary>
/// <remarks>
/// <para>
/// What it replaces, verbatim from a spec project whose fixtures did not match its features:
/// </para>
/// <code>
/// Test run summary: Zero tests ran - ShipmentTracking.Specs.dll
///   total: 0
///   failed: 0
///   succeeded: 0
/// </code>
/// <para>
/// Exit code 0, over a clean build. Nothing asserted anything and every signal available said the
/// run was fine — which is the failure this whole stack exists to prevent, a spec that cannot
/// fail, arriving by a route the closed step vocabulary had already shut. BOBCAT001 is now an
/// error so the specific cause is caught at build time; this catches it whatever the cause,
/// including causes no diagnostic can see.
/// </para>
/// </remarks>
public class EmptyRunTests
{
    private static FeatureDefinition feature(string title, params string[] scenarioTitles)
    {
        var scenarios = scenarioTitles
            .Select(t => new ScenarioDefinition(t, ["fast"], (_, plan) =>
                plan.Add(new DelegateExecutionStep("s", StepKind.Then, "it holds",
                    (_, _, _) => Task.CompletedTask))))
            .ToArray();

        return new FeatureDefinition(title, typeof(EmptyRunFixture), scenarios);
    }

    [Fact]
    public async Task a_run_that_discovered_nothing_fails_rather_than_passing()
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };

        var results = await runner.RunAll();

        results.ExitCode.ShouldBe(2);
        results.DiscoveryFailure.ShouldNotBeNull();
        results.DiscoveryFailure.ShouldContain("No specs were discovered");
    }

    [Fact]
    public async Task the_message_names_the_two_things_that_are_actually_wrong()
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };

        var results = await runner.RunAll();

        // A reader with zero specs has one of two wiring problems, and neither is guessable from
        // "0 tests". Naming both is the difference between a report and a shrug.
        results.DiscoveryFailure.ShouldContain("AdditionalFiles");
        results.DiscoveryFailure.ShouldContain("BOBCAT001");
    }

    [Fact]
    public async Task an_empty_run_can_still_be_opted_into()
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true, RequireSpecs = false };

        var results = await runner.RunAll();

        results.ExitCode.ShouldBe(0);
        results.DiscoveryFailure.ShouldBeNull();
    }

    [Fact]
    public async Task a_filter_matching_no_scenario_is_not_a_pass_either()
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(feature("Wallet", "Crediting"));

        var results = await runner.RunAll(featureFilter: "Ledger");

        results.ExitCode.ShouldBe(2);
        // The filter is quoted back, because the argument that selected nothing is the thing to
        // look at — a filter matching nothing is a typo far more often than an intention.
        results.DiscoveryFailure.ShouldContain("--feature");
        results.DiscoveryFailure.ShouldContain("Ledger");
    }

    [Fact]
    public async Task a_tag_filter_matching_nothing_says_which_tag()
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(feature("Wallet", "Crediting"));

        var results = await runner.RunAll(tagFilter: "regression");

        results.ExitCode.ShouldBe(2);
        results.DiscoveryFailure.ShouldContain("--tag");
        results.DiscoveryFailure.ShouldContain("regression");
    }

    [Fact]
    public async Task a_run_with_specs_is_untouched()
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(feature("Wallet", "Crediting", "Debiting"));

        var results = await runner.RunAll();

        results.DiscoveryFailure.ShouldBeNull();
        results.ExitCode.ShouldBe(0);
        results.AllScenarios.Count().ShouldBe(2);
    }

    [Fact]
    public async Task a_filter_that_does_match_is_untouched()
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(feature("Wallet", "Crediting"));
        runner.AddFeature(feature("Ledger", "Posting"));

        var results = await runner.RunAll(featureFilter: "Ledger");

        results.DiscoveryFailure.ShouldBeNull();
        results.ExitCode.ShouldBe(0);
        results.AllScenarios.Single().Title.ShouldBe("Posting");
    }
}
