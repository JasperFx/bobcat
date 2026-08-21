using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// Issue #123: a harness failure — a resource that will not start, a feature hook that throws —
/// must come back from <see cref="BobcatRunner.RunAll"/> as a reported catastrophic result,
/// never as an exception. An exception escaping RunAll takes an MTP host process down with it,
/// and whatever is driving that process can only read a dead process as a crash.
/// </summary>
public class HarnessFailureTests
{
    public class HarnessFixture : Fixture;

    private static readonly List<string> log = new();

    private static FeatureDefinition buildFeature(string title, params string[] scenarios)
        => buildFeature(title, null, null, scenarios);

    private static FeatureDefinition buildFeature(
        string title, Func<IStepContext, Task>? beforeAll, Func<IStepContext, Task>? afterAll,
        params string[] scenarios)
        => new(title, typeof(HarnessFixture),
            scenarios.Select(s => new ScenarioDefinition(s, [], (_, plan) =>
                plan.Add(new DelegateExecutionStep("step", StepKind.Then, s, (_, result, _) =>
                {
                    log.Add($"{title}/{s}");
                    return Task.CompletedTask;
                })))).ToArray())
        {
            BeforeAll = beforeAll,
            AfterAll = afterAll
        };

    private static BobcatRunner buildRunner(params FeatureDefinition[] features)
    {
        log.Clear();
        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        foreach (var feature in features) runner.AddFeature(feature);
        return runner;
    }

    [Fact]
    public async Task a_resource_that_fails_to_start_is_reported_as_catastrophic_not_thrown()
    {
        var runner = buildRunner(buildFeature("Orders", "places an order", "cancels an order"), buildFeature("Stock", "counts"));
        runner.Suite.AddResource(new LoggingResource("database"));
        runner.Suite.AddResource(new LoggingResource("broker", onStart: () => throw new InvalidOperationException("connection refused")));
        runner.Suite.AddResource(new LoggingResource("cache"));

        var results = await runner.RunAll();

        results.ExitCode.ShouldBe(2);
        results.CatastrophicFailure.ShouldNotBeNull();
        results.CatastrophicFailure.ShouldContain("Resource 'broker' failed to start");
        results.CatastrophicFailure.ShouldContain("connection refused");
        results.CatastrophicException.ShouldBeOfType<SpecCatastrophicException>();

        // Nothing ran, and nothing pretends it did: no feature results, no made-up scenarios.
        results.Features.ShouldBeEmpty();
        results.AllScenarios.ShouldBeEmpty();
        log.ShouldNotContain(entry => entry.Contains('/'), "no scenario step ran");

        // But every scenario the run planned is accounted for by name, with the reason.
        results.NotRun.Select(n => $"{n.FeatureTitle}/{n.Title}")
            .ShouldBe(["Orders/places an order", "Orders/cancels an order", "Stock/counts"]);
        results.NotRun.ShouldAllBe(n => n.Reason.Contains("connection refused"));
        results.NotRun.ShouldAllBe(n => n.Cause is SpecCatastrophicException);
    }

    [Fact]
    public async Task resources_that_started_are_torn_down_after_a_start_failure()
    {
        var runner = buildRunner(buildFeature("Orders", "places an order"));
        runner.Suite.AddResource(new LoggingResource("database"));
        runner.Suite.AddResource(new LoggingResource("broker", onStart: () => throw new InvalidOperationException("connection refused")));
        runner.Suite.AddResource(new LoggingResource("cache"));

        await runner.RunAll();

        // The one before it started and is disposed; the one that threw may be half up and gets
        // a dispose too; the one after it was never asked to start and is left alone.
        log.ShouldBe(["database:start", "broker:start", "broker:dispose", "database:dispose"]);
    }

    [Fact]
    public async Task a_teardown_failure_after_a_start_failure_is_appended_not_masking()
    {
        var runner = buildRunner(buildFeature("Orders", "places an order"));
        runner.Suite.AddResource(new LoggingResource("database", onDispose: () => throw new IOException("disk gone")));
        runner.Suite.AddResource(new LoggingResource("broker", onStart: () => throw new InvalidOperationException("connection refused")));

        var results = await runner.RunAll();

        results.ExitCode.ShouldBe(2);
        results.CatastrophicFailure.ShouldContain("connection refused");
        results.CatastrophicFailure.ShouldContain("disk gone");
        results.CatastrophicException.ShouldBeOfType<SpecCatastrophicException>();
    }

    [Fact]
    public async Task a_global_action_that_fails_during_set_up_is_catastrophic_and_resources_are_still_disposed()
    {
        var runner = buildRunner(buildFeature("Orders", "places an order"));
        runner.Suite.AddResource(new LoggingResource("database"));
        runner.Suite.AddGlobalAction(new ThrowingGlobalAction());

        var results = await runner.RunAll();

        results.ExitCode.ShouldBe(2);
        results.CatastrophicFailure.ShouldContain("ThrowingGlobalAction");
        results.NotRun.ShouldHaveSingleItem().Title.ShouldBe("places an order");
        log.ShouldBe(["database:start", "database:dispose"]);
    }

    [Fact]
    public async Task a_failing_preflight_lists_the_scenarios_it_kept_from_running()
    {
        var runner = buildRunner(buildFeature("Orders", "places an order", "cancels an order"));
        runner.Preflight.Add("docker is running", () => throw new InvalidOperationException("no docker"));

        var results = await runner.RunAll();

        results.PreflightFailure.ShouldNotBeNull();
        results.CatastrophicFailure.ShouldBeNull();
        results.NotRun.Select(n => n.Title).ShouldBe(["places an order", "cancels an order"]);
        results.NotRun.ShouldAllBe(n => n.Reason.Contains("no docker"));
    }

    [Fact]
    public async Task a_before_all_that_throws_fails_the_feature_and_the_run_moves_on()
    {
        var orders = buildFeature("Orders",
            beforeAll: _ => throw new InvalidOperationException("seed data missing"),
            afterAll: _ => { log.Add("Orders:AfterAll"); return Task.CompletedTask; },
            "places an order", "cancels an order");
        var runner = buildRunner(orders, buildFeature("Stock", "counts"));

        var results = await runner.RunAll();

        // The feature's scenarios did not run and say why; the next feature did run.
        results.ExitCode.ShouldBe(2);
        results.CatastrophicFailure.ShouldBeNull();

        var failed = results.Features.Single(f => f.Title == "Orders");
        failed.LifecycleFailure.ShouldContain("BeforeAll threw InvalidOperationException: seed data missing");
        failed.LifecycleException.ShouldBeOfType<InvalidOperationException>();
        failed.Scenarios.ShouldBeEmpty();
        failed.NotRun.Select(n => n.Title).ShouldBe(["places an order", "cancels an order"]);

        results.Features.Single(f => f.Title == "Stock").Scenarios.ShouldHaveSingleItem();

        // AfterAll still ran: a BeforeAll that got half-way is the one that leaves something to
        // clean up.
        log.ShouldBe(["Orders:AfterAll", "Stock/counts"]);
    }

    [Fact]
    public async Task an_after_all_that_throws_is_recorded_without_losing_the_scenario_results()
    {
        var orders = buildFeature("Orders",
            beforeAll: null,
            afterAll: _ => throw new InvalidOperationException("could not drop the schema"),
            "places an order");
        var runner = buildRunner(orders, buildFeature("Stock", "counts"));

        var results = await runner.RunAll();

        results.ExitCode.ShouldBe(2);
        var feature = results.Features.Single(f => f.Title == "Orders");
        feature.Scenarios.ShouldHaveSingleItem().Results.Counts.Succeeded.ShouldBeTrue();
        feature.LifecycleFailure.ShouldContain("AfterAll threw InvalidOperationException: could not drop the schema");
        results.NotRun.ShouldBeEmpty();
        log.ShouldBe(["Orders/places an order", "Stock/counts"]);
    }

    [Fact]
    public async Task a_catastrophic_exception_from_a_feature_hook_stops_the_suite()
    {
        var orders = buildFeature("Orders",
            beforeAll: _ => throw new SpecCatastrophicException("the environment is unusable"),
            afterAll: _ => { log.Add("Orders:AfterAll"); return Task.CompletedTask; },
            "places an order");
        var runner = buildRunner(orders, buildFeature("Stock", "counts"));
        runner.Suite.AddResource(new LoggingResource("database"));

        var results = await runner.RunAll();

        results.ExitCode.ShouldBe(2);
        results.CatastrophicFailure.ShouldBe("the environment is unusable");
        results.NotRun.Select(n => $"{n.FeatureTitle}/{n.Title}").ShouldBe(["Orders/places an order", "Stock/counts"]);

        // AfterAll for the feature in flight, then the resources — the suite still shuts down.
        log.ShouldBe(["database:start", "Orders:AfterAll", "database:dispose"]);
    }

    [Fact]
    public async Task a_harness_failure_mid_feature_keeps_what_ran_and_accounts_for_the_rest()
    {
        // ResetBetweenScenarios throwing is not a step failure and not a hook failure — it is the
        // kind of thing the last line of defence exists for.
        var resets = 0;
        var runner = buildRunner(buildFeature("Orders", "first", "second", "third"), buildFeature("Stock", "counts"));
        runner.Suite.AddResource(new LoggingResource("database", onReset: () =>
        {
            if (++resets == 2) throw new TimeoutException("truncate timed out");
        }));

        var results = await runner.RunAll();

        results.ExitCode.ShouldBe(2);
        results.CatastrophicFailure.ShouldBe("TimeoutException: truncate timed out");
        results.CatastrophicException.ShouldBeOfType<TimeoutException>();

        // The first scenario's result survives; the rest are accounted for, nothing twice.
        results.Features.Single(f => f.Title == "Orders").Scenarios.Select(s => s.Title).ShouldBe(["first"]);
        results.Counts.Rights.ShouldBe(1);
        results.NotRun.Select(n => $"{n.FeatureTitle}/{n.Title}")
            .ShouldBe(["Orders/second", "Orders/third", "Stock/counts"]);

        log.ShouldContain("database:dispose");
    }

    [Fact]
    public async Task the_observer_still_sees_the_run_finish_with_the_catastrophic_results()
    {
        var runner = buildRunner(buildFeature("Orders", "places an order"));
        runner.Suite.AddResource(new LoggingResource("broker", onStart: () => throw new InvalidOperationException("connection refused")));
        var observer = new RecordingObserver();
        runner.WithObserver(observer);

        await runner.RunAll();

        observer.Started.ShouldBe(1);
        observer.Finished.ShouldNotBeNull();
        observer.Finished.CatastrophicFailure.ShouldContain("connection refused");
        observer.Finished.NotRun.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task the_summary_renders_a_catastrophic_run_without_throwing()
    {
        // The in-process CLI path: BobcatRunner.Run renders the summary and returns ExitCode.
        var runner = buildRunner(buildFeature("Orders", "places an order"));
        runner.Suite.AddResource(new LoggingResource("broker", onStart: () => throw new InvalidOperationException("connection refused")));

        var results = await runner.RunAll();

        Should.NotThrow(() => runner.RenderSummary(results));
        results.ExitCode.ShouldBe(2);
    }

    [Fact]
    public async Task the_json_report_carries_the_failure_and_the_scenarios_that_did_not_run()
    {
        var runner = buildRunner(buildFeature("Orders", "places an order"));
        runner.Suite.AddResource(new LoggingResource("broker", onStart: () => throw new InvalidOperationException("connection refused")));

        var results = await runner.RunAll();
        var json = System.Text.Json.JsonDocument.Parse(Bobcat.Rendering.JsonRenderer.RenderSuite(results)).RootElement;

        json.GetProperty("exitCode").GetInt32().ShouldBe(2);
        json.GetProperty("catastrophicFailure").GetString()
            .ShouldBe("Resource 'broker' failed to start: connection refused");

        var notRun = json.GetProperty("notRun").EnumerateArray().Single();
        notRun.GetProperty("feature").GetString().ShouldBe("Orders");
        notRun.GetProperty("title").GetString().ShouldBe("places an order");
        notRun.GetProperty("reason").GetString().ShouldContain("connection refused");
    }

    private sealed class RecordingObserver : IExecutionObserver
    {
        public int Started;
        public SuiteResults? Finished;

        public void RunStarted(int totalScenarios) => Started++;
        public void RunFinished(SuiteResults results) => Finished = results;
        public void FeatureStarted(string featureTitle) { }
        public void FeatureFinished(string featureTitle) { }
        public void ScenarioStarted(string featureTitle, string scenarioTitle) { }
        public void StepStarted(string stepId, StepKind kind, string stepText) { }
        public void StepProgress(string stepId, StepUpdate update) { }
        public void StepFinished(StepResult result) { }
        public void ScenarioFinished(ExecutionResults results) { }
    }

    private sealed class ThrowingGlobalAction : IGlobalAction
    {
        public Task SetUp() => throw new InvalidOperationException("seeding failed");
        public Task TearDown() => Task.CompletedTask;
    }

    private sealed class LoggingResource(
        string name, Action? onStart = null, Action? onReset = null, Action? onDispose = null) : ITestResource
    {
        public string Name { get; } = name;

        public Task Start()
        {
            log.Add($"{Name}:start");
            onStart?.Invoke();
            return Task.CompletedTask;
        }

        public Task ResetBetweenScenarios()
        {
            onReset?.Invoke();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            log.Add($"{Name}:dispose");
            onDispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
