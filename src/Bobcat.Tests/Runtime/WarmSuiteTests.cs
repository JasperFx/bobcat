using Bobcat.Engine;
using Bobcat.Runtime;
using Bobcat.Runtime.Commands;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// Issue #209: the interactive command holds the suite warm between selections — StartAll
/// once, DisposeAsync once on exit — while every selected run still gets the full per-scenario
/// ResetAll bracket, so warmth never means dirty state.
/// </summary>
public class WarmSuiteTests
{
    public class WarmFixture : Fixture;

    private static readonly List<string> log = new();

    public WarmSuiteTests() => log.Clear();

    private sealed class CountingResource : ITestResource
    {
        public int Started, Resets, Disposed;
        public bool FailOnStart;

        public string Name => "counting";

        public Task Start()
        {
            if (FailOnStart) throw new InvalidOperationException("connection refused");
            Started++;
            return Task.CompletedTask;
        }

        public Task ResetBetweenScenarios()
        {
            Resets++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }

    private static FeatureDefinition buildFeature(string title, params string[] scenarios)
        => new(title, typeof(WarmFixture),
            scenarios.Select(s => new ScenarioDefinition(s, [], (_, plan) =>
                plan.Add(new DelegateExecutionStep("step", StepKind.Then, s, (_, result, _) =>
                {
                    log.Add($"{title}/{s}");
                    result.MarkSuccess();
                    return Task.CompletedTask;
                })))).ToArray());

    private static BobcatRunner buildRunner(CountingResource resource, params FeatureDefinition[] features)
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        foreach (var feature in features) runner.AddFeature(feature);
        runner.Suite.AddResource(resource);
        return runner;
    }

    [Fact]
    public async Task resources_start_once_across_selections_and_dispose_once_on_stop()
    {
        var resource = new CountingResource();
        var runner = buildRunner(resource, buildFeature("Orders", "places", "cancels"));

        (await runner.StartWarmSuite()).ShouldBeNull();

        var first = await runner.RunWarmSelection(null, null);
        var second = await runner.RunWarmSelection(null, null);
        await runner.StopWarmSuite();

        resource.Started.ShouldBe(1, "StartAll is paid once per session, not per selection");
        resource.Disposed.ShouldBe(1);
        first.ExitCode.ShouldBe(0);
        second.ExitCode.ShouldBe(0);
        log.ShouldBe(["Orders/places", "Orders/cancels", "Orders/places", "Orders/cancels"]);

        // Warmth never means dirty state: every scenario of every selection was preceded by
        // its own ResetBetweenScenarios.
        resource.Resets.ShouldBe(4);
    }

    [Fact]
    public async Task a_selection_narrows_to_the_chosen_scenario_with_its_own_bracket()
    {
        var resource = new CountingResource();
        var orders = buildFeature("Orders", "places", "cancels");
        var runner = buildRunner(resource, orders, buildFeature("Stock", "counts"));

        (await runner.StartWarmSuite()).ShouldBeNull();

        var choice = InteractiveChoice.ForScenario(orders, orders.Scenarios[1]);
        var results = await runner.RunWarmSelection(null, null, choice.Matches);
        await runner.StopWarmSuite();

        log.ShouldBe(["Orders/cancels"]);
        resource.Resets.ShouldBe(1);
        results.AllScenarios.Single().Title.ShouldBe("cancels");
    }

    [Fact]
    public async Task a_whole_feature_choice_runs_all_of_its_scenarios_and_no_other_feature()
    {
        var resource = new CountingResource();
        var orders = buildFeature("Orders", "places", "cancels");
        var runner = buildRunner(resource, orders, buildFeature("Stock", "counts"));

        (await runner.StartWarmSuite()).ShouldBeNull();
        var choice = InteractiveChoice.ForFeature(orders, 2);
        await runner.RunWarmSelection(null, null, choice.Matches);
        await runner.StopWarmSuite();

        log.ShouldBe(["Orders/places", "Orders/cancels"]);
    }

    [Fact]
    public async Task a_selection_that_skips_a_feature_never_runs_its_lifecycle_hooks()
    {
        var resource = new CountingResource();
        var orders = buildFeature("Orders", "places");
        var stock = new FeatureDefinition("Stock", typeof(WarmFixture),
            [new ScenarioDefinition("counts", [], (_, plan) =>
                plan.Add(new DelegateExecutionStep("step", StepKind.Then, "counts", (_, result, _) =>
                {
                    result.MarkSuccess();
                    return Task.CompletedTask;
                })))])
        {
            BeforeAll = _ =>
            {
                log.Add("Stock.BeforeAll");
                return Task.CompletedTask;
            }
        };
        var runner = buildRunner(resource, orders, stock);

        (await runner.StartWarmSuite()).ShouldBeNull();
        var choice = InteractiveChoice.ForFeature(orders, 1);
        await runner.RunWarmSelection(null, null, choice.Matches);
        await runner.StopWarmSuite();

        log.ShouldNotContain("Stock.BeforeAll", "an untouched feature's BeforeAll must not run");
    }

    [Fact]
    public async Task a_failed_warm_start_reports_the_reason_and_tears_down()
    {
        var resource = new CountingResource { FailOnStart = true };
        var runner = buildRunner(resource, buildFeature("Orders", "places"));

        var failure = await runner.StartWarmSuite();

        failure.ShouldNotBeNull();
        failure.ShouldContain("connection refused");
        resource.Disposed.ShouldBe(1, "the resource that tried to start still gets disposed");
    }

    [Fact]
    public async Task the_scenario_filter_is_restored_after_a_selection()
    {
        var resource = new CountingResource();
        var orders = buildFeature("Orders", "places", "cancels");
        var runner = buildRunner(resource, orders);

        Func<FeatureDefinition, ScenarioDefinition, bool> preset = (_, s) => s.Title == "places";
        runner.ScenarioFilter = preset;

        (await runner.StartWarmSuite()).ShouldBeNull();

        // The selection composes WITH the preset filter (both must agree)…
        var choice = InteractiveChoice.ForScenario(orders, orders.Scenarios[1]);
        await runner.RunWarmSelection(null, null, choice.Matches);
        log.ShouldBeEmpty("'cancels' is outside the preset filter");

        // …and the preset filter survives the selection untouched.
        runner.ScenarioFilter.ShouldBeSameAs(preset);
        await runner.StopWarmSuite();
    }
}

/// <summary>The pure pieces of the interactive command: the choice tree and the TTY gate.</summary>
public class InteractiveChoicesTests
{
    public class ChoiceFixture : Fixture;

    private static FeatureDefinition feature(string title, params string[] scenarios)
        => new(title, typeof(ChoiceFixture),
            scenarios.Select(s => new ScenarioDefinition(s, [], (_, _) => { })).ToArray());

    [Fact]
    public void builds_a_feature_row_then_its_scenarios_then_exit()
    {
        var orders = feature("Orders", "places", "cancels");

        var choices = InteractiveChoices.Build([(orders, orders.Scenarios.ToArray())]);

        choices.Count.ShouldBe(4);
        choices[0].Label.ShouldContain("Orders — all 2");
        choices[1].Label.ShouldContain("places");
        choices[2].Label.ShouldContain("cancels");
        choices[3].IsExit.ShouldBeTrue();
    }

    [Fact]
    public void a_feature_choice_matches_all_its_scenarios_and_nothing_else()
    {
        var orders = feature("Orders", "places");
        var stock = feature("Stock", "counts");

        var choice = InteractiveChoice.ForFeature(orders, 1);

        choice.Matches(orders, orders.Scenarios[0]).ShouldBeTrue();
        choice.Matches(stock, stock.Scenarios[0]).ShouldBeFalse();
    }

    [Fact]
    public void a_scenario_choice_matches_only_itself()
    {
        var orders = feature("Orders", "places", "cancels");

        var choice = InteractiveChoice.ForScenario(orders, orders.Scenarios[0]);

        choice.Matches(orders, orders.Scenarios[0]).ShouldBeTrue();
        choice.Matches(orders, orders.Scenarios[1]).ShouldBeFalse();
    }

    [Fact]
    public void the_tty_gate_refuses_every_non_interactive_console_with_a_reason()
    {
        InteractiveCommand.DescribeNonInteractiveConsole(false, false, true).ShouldBeNull();

        InteractiveCommand.DescribeNonInteractiveConsole(true, false, true)
            .ShouldNotBeNull().ShouldContain("standard input is redirected");
        InteractiveCommand.DescribeNonInteractiveConsole(false, true, true)
            .ShouldNotBeNull().ShouldContain("standard output is redirected");
        InteractiveCommand.DescribeNonInteractiveConsole(false, false, false)
            .ShouldNotBeNull().ShouldContain("not interactive");
    }
}
