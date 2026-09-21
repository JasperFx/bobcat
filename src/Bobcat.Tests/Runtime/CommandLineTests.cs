using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// Issue #206: <see cref="BobcatRunner.Run"/> delegates to a JasperFx command family, and the
/// things a consumer scripted against must survive verbatim — the exit-code contract
/// (0 pass / 1 regression / 2 catastrophic), the <c>--feature</c>/<c>--tag</c>/<c>--json</c>
/// flags, and <c>list</c> never executing anything.
/// </summary>
public class CommandLineTests
{
    public class CommandLineFixture : Fixture;

    private static readonly List<string> log = new();

    public CommandLineTests()
    {
        // Run() is the real entry point and turns monitor publishing on. Pointing the probe at
        // the discard port keeps these tests from finding a live console on the developer's box
        // — the kill switch would work too, but it is process-wide and MonitorPublisherTests
        // runs concurrently and needs it clear.
        Environment.SetEnvironmentVariable("BOBCAT_MONITOR_URL", "http://127.0.0.1:9");
        log.Clear();
    }

    private static FeatureDefinition buildFeature(string title, bool passes = true, params string[] scenarios)
        => new(title, typeof(CommandLineFixture),
            scenarios.Select(s => new ScenarioDefinition(s, [], (_, plan) =>
                plan.Add(new DelegateExecutionStep("step", StepKind.Then, s, (_, result, _) =>
                {
                    log.Add($"{title}/{s}");
                    if (!passes) throw new InvalidOperationException("deliberate failure");
                    result.MarkSuccess();
                    return Task.CompletedTask;
                })))).ToArray());

    [Fact]
    public async Task no_arguments_runs_everything_and_exits_zero()
    {
        var code = await BobcatRunner.Run(["--json"],
            r => r.AddFeature(buildFeature("Orders", passes: true, "places", "cancels")));

        code.ShouldBe(0);
        log.ShouldBe(["Orders/places", "Orders/cancels"]);
    }

    [Fact]
    public async Task explicit_run_command_works_the_same()
    {
        var code = await BobcatRunner.Run(["run", "--json"],
            r => r.AddFeature(buildFeature("Orders", passes: true, "places")));

        code.ShouldBe(0);
        log.ShouldBe(["Orders/places"]);
    }

    [Fact]
    public async Task a_regression_failure_exits_one_not_jasperfx_something_else()
    {
        var code = await BobcatRunner.Run(["run", "--json"],
            r => r.AddFeature(buildFeature("Orders", passes: false, "places")));

        code.ShouldBe(1);
    }

    [Fact]
    public async Task a_catastrophic_harness_failure_exits_two()
    {
        var code = await BobcatRunner.Run(["run", "--json"], r =>
        {
            r.AddFeature(buildFeature("Orders", passes: true, "places"));
            r.Suite.AddResource(new FailingResource());
        });

        // JasperFx maps a command to 0/1; the 2 must come from the recorded SuiteResults verdict.
        code.ShouldBe(2);
        log.ShouldBeEmpty();
    }

    private sealed class FailingResource : ITestResource
    {
        public string Name => "broker";
        public Task StartAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("connection refused");
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetBetweenScenarios() => Task.CompletedTask;
    }

    [Fact]
    public async Task feature_filter_narrows_the_run()
    {
        var code = await BobcatRunner.Run(["run", "--json", "--feature", "Stock"], r =>
        {
            r.AddFeature(buildFeature("Orders", passes: true, "places"));
            r.AddFeature(buildFeature("Stock", passes: true, "counts"));
        });

        code.ShouldBe(0);
        log.ShouldBe(["Stock/counts"]);
    }

    [Fact]
    public async Task tag_filter_narrows_the_run()
    {
        var tagged = new FeatureDefinition("Orders", typeof(CommandLineFixture),
        [
            new ScenarioDefinition("slow one", ["slow"], (_, plan) =>
                plan.Add(new DelegateExecutionStep("step", StepKind.Then, "slow", (_, result, _) =>
                {
                    log.Add("slow one");
                    result.MarkSuccess();
                    return Task.CompletedTask;
                }))),
            new ScenarioDefinition("fast one", [], (_, plan) =>
                plan.Add(new DelegateExecutionStep("step", StepKind.Then, "fast", (_, result, _) =>
                {
                    log.Add("fast one");
                    result.MarkSuccess();
                    return Task.CompletedTask;
                })))
        ]);

        var code = await BobcatRunner.Run(["run", "--json", "--tag", "slow"], r => r.AddFeature(tagged));

        code.ShouldBe(0);
        log.ShouldBe(["slow one"]);
    }

    /// <summary>
    /// Issue #370. The '@' is stripped by the Gherkin parser, so a stored tag never carries one —
    /// but the spelling a user copies out of their own .feature file always does. Both forms have
    /// to select the same scenarios; `@slow` used to match nothing at all, and on `list` that
    /// printed a feature header with no scenarios under it rather than an empty result.
    /// </summary>
    [Theory]
    [InlineData("slow")]
    [InlineData("@slow")]
    [InlineData("SLOW")]
    [InlineData("@SLOW")]
    public async Task tag_filter_accepts_the_at_prefixed_spelling(string filter)
    {
        var tagged = new FeatureDefinition("Orders", typeof(CommandLineFixture),
        [
            new ScenarioDefinition("slow one", ["slow"], (_, plan) =>
                plan.Add(new DelegateExecutionStep("step", StepKind.Then, "slow", (_, result, _) =>
                {
                    log.Add("slow one");
                    result.MarkSuccess();
                    return Task.CompletedTask;
                }))),
            new ScenarioDefinition("fast one", [], (_, plan) =>
                plan.Add(new DelegateExecutionStep("step", StepKind.Then, "fast", (_, result, _) =>
                {
                    log.Add("fast one");
                    result.MarkSuccess();
                    return Task.CompletedTask;
                })))
        ]);

        var code = await BobcatRunner.Run(["run", "--json", "--tag", filter], r => r.AddFeature(tagged));

        code.ShouldBe(0);
        log.ShouldBe(["slow one"]);
    }

    /// <summary>
    /// Issue #369. `run` reported an undiscoverable suite loudly and exited 2 while `list` and
    /// `preview` printed nothing at all and exited 0 — so the two commands a person reaches for
    /// first when a project is misconfigured were the two that said nothing. They still exit 0;
    /// they no longer do it in silence.
    /// </summary>
    [Theory]
    [InlineData("list")]
    [InlineData("preview")]
    public async Task list_and_preview_report_a_suite_with_nothing_discovered(string command)
    {
        var console = new StringWriter();
        var original = Console.Out;
        Console.SetOut(console);
        try
        {
            var code = await BobcatRunner.Run([command], _ => { });
            code.ShouldBe(0);
            console.ToString().ShouldContain("No specs were discovered");
        }
        finally { Console.SetOut(original); }
    }

    /// <summary>
    /// Issue #369 / #370. A filter that selects nothing quotes itself back, rather than rendering
    /// as an empty page the reader has to interpret.
    /// </summary>
    [Theory]
    [InlineData("list")]
    [InlineData("preview")]
    public async Task list_and_preview_report_a_filter_that_matched_nothing(string command)
    {
        var console = new StringWriter();
        var original = Console.Out;
        Console.SetOut(console);
        try
        {
            var code = await BobcatRunner.Run([command, "--tag", "nosuchtag"],
                r => r.AddFeature(buildFeature("Orders", passes: true, "places")));

            code.ShouldBe(0);
            var text = console.ToString();
            text.ShouldContain("Nothing matched");
            text.ShouldContain("nosuchtag");
            text.ShouldNotContain("Feature: Orders");
        }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public async Task list_executes_nothing_and_exits_zero()
    {
        var code = await BobcatRunner.Run(["list"],
            r => r.AddFeature(buildFeature("Orders", passes: false, "places")));

        code.ShouldBe(0);
        log.ShouldBeEmpty("list must never execute a scenario");
    }

    [Fact]
    public async Task preview_executes_nothing_and_never_starts_a_resource()
    {
        var code = await BobcatRunner.Run(["preview"], r =>
        {
            r.AddFeature(buildFeature("Orders", passes: false, "places"));

            // The resource throws on Start, so exit 0 is only reachable if preview never
            // started it — the same never-start rule as MTP discovery.
            r.Suite.AddResource(new FailingResource());
        });

        code.ShouldBe(0);
        log.ShouldBeEmpty("preview must never execute a scenario");
    }

    [Fact]
    public async Task an_unknown_flag_now_errors_instead_of_being_silently_ignored()
    {
        var code = await BobcatRunner.Run(["run", "--bogus"],
            r => r.AddFeature(buildFeature("Orders", passes: true, "places")));

        code.ShouldNotBe(0);
        log.ShouldBeEmpty("nothing runs on an invalid command line");
    }
}
