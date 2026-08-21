using System.Text.Json;
using Bobcat.Console.Contracts;
using Bobcat.Console.Mcp;
using Bobcat.Console.Runs;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// The MCP tools tested as what they are — static functions from a registry to JSON — with
/// no MCP transport in the loop. The transport wiring is exercised by the live host smoke.
/// </summary>
public class MonitorToolsTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"bobcat-mcp-{Guid.NewGuid():N}");
    private readonly MonitorRunRegistry _registry;

    private static readonly Guid finishedRun = Guid.NewGuid();
    private static readonly Guid runningRun = Guid.NewGuid();

    public MonitorToolsTests()
    {
        _registry = new MonitorRunRegistry(_dataPath);

        var t0 = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        _registry.Record(
        [
            new RunStarted(finishedRun, "Older", "/repo-a", "main", "in-process", t0, 2),
            new ScenarioStarted(finishedRun, "F/clean", "F", "clean", 1, t0),
            new ScenarioFinished(finishedRun, "F/clean", "CleanPass", 1, 10, null),
            new ScenarioStarted(finishedRun, "F/flaky", "F", "flaky", 1, t0),
            new RetryScheduled(finishedRun, "F/flaky", 2, "RetryInProcess", "slow broker"),
            new ScenarioStarted(finishedRun, "F/flaky", "F", "flaky", 2, t0),
            new ScenarioFinished(finishedRun, "F/flaky", "PassOnRetry", 2, 900, null),
            new RunFinished(finishedRun, 0, 1, 0, 1, 0, t0.AddMinutes(1)),

            // A newer run still in flight, with one failure and one live scenario.
            new RunStarted(runningRun, "Newer", "/repo-b", "dev", "mtp-host", t0.AddMinutes(5), 3),
            new ScenarioStarted(runningRun, "G/bad", "G", "bad", 1, t0.AddMinutes(5)),
            new StepStarted(runningRun, "G/bad", "s1", "Then", "the result should be 7"),
            new StepFinished(runningRun, "G/bad", "s1", "failed", 3, "expected 7 but was 8"),
            new ScenarioFinished(runningRun, "G/bad", "Failed", 1, 12, "expected 7 but was 8"),
            new ScenarioStarted(runningRun, "G/live", "G", "live", 1, t0.AddMinutes(5)),
            new StepStarted(runningRun, "G/live", "s1", "Given", "a long setup")
        ]);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    private static JsonElement parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void list_runs_returns_newest_first_with_honest_counts()
    {
        var runs = parse(MonitorTools.ListRuns(_registry)).EnumerateArray().ToArray();

        runs.Length.ShouldBe(2);
        runs[0].GetProperty("suite").GetString().ShouldBe("Newer");
        runs[0].GetProperty("finished").GetBoolean().ShouldBeFalse();
        runs[0].GetProperty("counts").GetProperty("running").GetInt32().ShouldBe(1);
        runs[1].GetProperty("suite").GetString().ShouldBe("Older");
        runs[1].GetProperty("counts").GetProperty("passedOnRetry").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void run_status_defaults_to_the_most_recent_run_and_shows_live_steps()
    {
        var status = parse(MonitorTools.RunStatus(_registry));

        status.GetProperty("run").GetProperty("suite").GetString().ShouldBe("Newer");

        var scenarios = status.GetProperty("scenarios").EnumerateArray().ToArray();
        var live = scenarios.Single(s => s.GetProperty("uid").GetString() == "G/live");
        live.GetProperty("status").GetString().ShouldBe("running");
        live.GetProperty("steps")[0].GetProperty("name").GetString().ShouldBe("Given a long setup");

        // Finished scenarios summarize — no step spam.
        scenarios.Single(s => s.GetProperty("uid").GetString() == "G/bad")
            .GetProperty("steps").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void failing_tests_surfaces_errors_and_failed_steps()
    {
        var failing = parse(MonitorTools.FailingTests(_registry, runningRun.ToString()))
            .GetProperty("failing").EnumerateArray().ToArray();

        var bad = failing.ShouldHaveSingleItem();
        bad.GetProperty("uid").GetString().ShouldBe("G/bad");
        bad.GetProperty("errorMessage").GetString().ShouldBe("expected 7 but was 8");
        bad.GetProperty("failedSteps")[0].GetProperty("name").GetString()
            .ShouldBe("Then the result should be 7");
    }

    [Fact]
    public void flaky_ledger_spans_runs_and_carries_retry_reasons()
    {
        var ledger = parse(MonitorTools.FlakyLedger(_registry)).EnumerateArray().ToArray();

        var entry = ledger.ShouldHaveSingleItem();
        entry.GetProperty("uid").GetString().ShouldBe("F/flaky");
        entry.GetProperty("suite").GetString().ShouldBe("Older");
        entry.GetProperty("attempts").GetInt32().ShouldBe(2);
        entry.GetProperty("retryReasons")[0].GetString().ShouldBe("slow broker");
    }

    [Fact]
    public void export_run_renders_ctrf_for_the_requested_run()
    {
        var ctrf = parse(MonitorTools.ExportRun(_registry, finishedRun.ToString()));
        ctrf.GetProperty("reportFormat").GetString().ShouldBe("CTRF");
        ctrf.GetProperty("results").GetProperty("summary").GetProperty("tests").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task await_run_completion_blocks_until_the_run_finished_event_lands()
    {
        // The only in-flight run is runningRun, so no runId is needed.
        var finishSoon = Task.Run(async () =>
        {
            await Task.Delay(300);
            _registry.Record(
            [
                new ScenarioFinished(runningRun, "G/live", "CleanPass", 1, 800, null),
                new RunFinished(runningRun, 1, 1, 1, 0, 0, DateTimeOffset.UtcNow)
            ]);
        });

        var result = parse(await MonitorTools.AwaitRunCompletion(_registry, timeoutSeconds: 30));
        await finishSoon;

        result.GetProperty("outcome").GetString().ShouldBe("finished");
        result.GetProperty("run").GetProperty("exitCode").GetInt32().ShouldBe(1);
        result.GetProperty("run").GetProperty("counts").GetProperty("failed").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task await_run_completion_times_out_with_current_progress_not_an_exception()
    {
        var result = parse(await MonitorTools.AwaitRunCompletion(
            _registry, runningRun.ToString(), timeoutSeconds: 1));

        result.GetProperty("outcome").GetString().ShouldBe("timeout");
        result.GetProperty("run").GetProperty("counts").GetProperty("running").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task await_run_completion_returns_the_latest_run_when_nothing_is_in_flight()
    {
        _registry.Record(
        [
            new ScenarioFinished(runningRun, "G/live", "CleanPass", 1, 800, null),
            new RunFinished(runningRun, 1, 1, 1, 0, 0, DateTimeOffset.UtcNow)
        ]);

        var result = parse(await MonitorTools.AwaitRunCompletion(_registry, timeoutSeconds: 5));
        result.GetProperty("outcome").GetString().ShouldBe("finished");
        result.GetProperty("run").GetProperty("suite").GetString().ShouldBe("Newer");
    }

    [Fact]
    public void bad_run_ids_come_back_as_error_payloads_not_exceptions()
    {
        parse(MonitorTools.RunStatus(_registry, "not-a-guid")).GetProperty("error").GetString()
            .ShouldContain("not a run id");
        parse(MonitorTools.ExportRun(_registry, format: "yaml")).GetProperty("error").GetString()
            .ShouldContain("unknown format");
    }
}
