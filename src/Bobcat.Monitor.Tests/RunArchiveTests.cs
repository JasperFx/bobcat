using System.Text.Json;
using System.Xml.Linq;
using Bobcat.Monitor.Contracts;
using Bobcat.Monitor.Runs;
using Shouldly;

namespace Bobcat.Monitor.Tests;

/// <summary>
/// One realistic run folded three ways: into the projection, onto disk as NDJSON, and out
/// through the CTRF and JUnit exporters. The event stream: a clean pass, a flaky scenario that
/// passes on retry, a failure, and one scenario with no terminal outcome (publisher died).
/// </summary>
public class RunArchiveTests : IDisposable
{
    private static readonly Guid runId = Guid.NewGuid();
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"bobcat-archive-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    private static IReadOnlyList<MonitorEvent> realisticRun()
    {
        var t0 = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        return
        [
            new RunStarted(runId, "Demo", "/repo", "main", "in-process", t0, 4),

            new ScenarioStarted(runId, "Calc/adds", "Calc", "adds", 1, t0),
            new StepStarted(runId, "Calc/adds", "s1", "Given", "a calculator"),
            new StepFinished(runId, "Calc/adds", "s1", "success", 5, null),
            new ScenarioFinished(runId, "Calc/adds", "CleanPass", 1, 20, null),

            new ScenarioStarted(runId, "Orders/broker", "Orders", "broker", 1, t0),
            new StepStarted(runId, "Orders/broker", "s1", "When", "the broker is asked"),
            new StepFinished(runId, "Orders/broker", "s1", "error", 5000, "TimeoutException"),
            new RetryScheduled(runId, "Orders/broker", 2, "RetryInProcess", "broker warms up slowly"),
            new ScenarioStarted(runId, "Orders/broker", "Orders", "broker", 2, t0),
            new StepStarted(runId, "Orders/broker", "s1", "When", "the broker is asked"),
            new StepFinished(runId, "Orders/broker", "s1", "success", 90, null),
            new ScenarioFinished(runId, "Orders/broker", "PassOnRetry", 2, 6000, null),

            new ScenarioStarted(runId, "Calc/bad", "Calc", "bad", 1, t0),
            new StepStarted(runId, "Calc/bad", "s1", "Then", "the result should be 7"),
            new StepFinished(runId, "Calc/bad", "s1", "failed", 3, "expected 7 but was 8"),
            new ScenarioFinished(runId, "Calc/bad", "Failed", 1, 10, "expected 7 but was 8"),

            // Started but never finished — the honest "pending" case.
            new ScenarioStarted(runId, "Calc/lost", "Calc", "lost", 1, t0),

            new RunFinished(runId, 1, 1, 1, 1, 0, t0.AddMinutes(1))
        ];
    }

    private static RunProjection project()
    {
        var projection = new RunProjection(runId);
        foreach (var @event in realisticRun()) projection.Apply(@event);
        return projection;
    }

    [Fact]
    public void the_projection_folds_the_stream_including_retries_and_step_resets()
    {
        var run = project();

        run.Suite.ShouldBe("Demo");
        run.Finished.ShouldBeTrue();
        run.ExitCode.ShouldBe(1);
        run.Scenarios.Count.ShouldBe(4);

        var flaky = run.Scenarios.Single(s => s.Uid == "Orders/broker");
        flaky.Outcome.ShouldBe("PassOnRetry");
        flaky.Attempts.ShouldBe(2);
        flaky.RetryReasons.ShouldBe(["broker warms up slowly"]);
        // The retry reset the live step list — only the second attempt's step remains here...
        flaky.Steps.ShouldHaveSingleItem().Status.ShouldBe("success");

        // ...but the first attempt's history survived the reset, with the policy's verdict.
        var prior = flaky.PriorAttempts.ShouldHaveSingleItem();
        prior.Attempt.ShouldBe(1);
        prior.Steps.ShouldHaveSingleItem().Status.ShouldBe("error");
        prior.ErrorMessage.ShouldBe("TimeoutException");
        prior.Disposition.ShouldBe("RetryInProcess");
        prior.Reason.ShouldBe("broker warms up slowly");

        run.Scenarios.Single(s => s.Uid == "Calc/adds").PriorAttempts.ShouldBeEmpty();
        run.Scenarios.Single(s => s.Uid == "Calc/lost").Outcome.ShouldBeNull();
    }

    [Fact]
    public void the_registry_archives_every_event_as_reingestable_ndjson()
    {
        using var registry = new MonitorRunRegistry(_dataPath);
        registry.Record(realisticRun());

        var file = registry.ArchiveFileFor(runId);
        File.Exists(file).ShouldBeTrue();

        var lines = File.ReadAllLines(file);
        lines.Length.ShouldBe(realisticRun().Count);

        // Every archived line deserializes straight back into the polymorphic contract —
        // byte-for-byte re-ingestable, which is what makes replay-for-debugging free.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var replayed = lines.Select(l => JsonSerializer.Deserialize<MonitorEvent>(l, options)!).ToArray();
        replayed.First().ShouldBeOfType<RunStarted>();
        replayed.Last().ShouldBeOfType<RunFinished>();

        var reprojected = new RunProjection(runId);
        foreach (var @event in replayed) reprojected.Apply(@event);
        reprojected.Scenarios.Count.ShouldBe(4);
    }

    [Fact]
    public void ejecting_forgets_the_run_and_moves_the_archive_aside_never_deleting_it()
    {
        using var registry = new MonitorRunRegistry(_dataPath);
        registry.Record(realisticRun());

        registry.Remove(runId).ShouldBeTrue();
        registry.Find(runId).ShouldBeNull();

        // The archive moved to ejected/ (so boot rehydration skips it) — data never deleted.
        File.Exists(registry.ArchiveFileFor(runId)).ShouldBeFalse();
        File.Exists(registry.EjectedFileFor(runId)).ShouldBeTrue();
        File.ReadAllLines(registry.EjectedFileFor(runId)).Length.ShouldBe(realisticRun().Count);

        // A run re-appearing after its eject starts a fresh archive in the live folder.
        registry.Record([new RunHeartbeat(runId, DateTimeOffset.UtcNow)]);
        File.ReadAllLines(registry.ArchiveFileFor(runId)).Length.ShouldBe(1);
    }

    [Fact]
    public void a_restarted_registry_rehydrates_finished_runs_from_the_archives()
    {
        using (var first = new MonitorRunRegistry(_dataPath))
        {
            first.Record(realisticRun());
        }

        using var restarted = new MonitorRunRegistry(_dataPath);

        var run = restarted.Find(runId).ShouldNotBeNull();
        run.Suite.ShouldBe("Demo");
        run.Finished.ShouldBeTrue();
        run.Orphaned.ShouldBeFalse();
        run.Scenarios.Count.ShouldBe(4);
        run.Scenarios.Single(s => s.Uid == "Orders/broker").Outcome.ShouldBe("PassOnRetry");
    }

    [Fact]
    public void a_rehydrated_run_with_no_terminal_event_is_orphaned_until_it_publishes_again()
    {
        using (var first = new MonitorRunRegistry(_dataPath))
        {
            // Everything except the RunFinished — the publisher died mid-run.
            first.Record(realisticRun().SkipLast(1).ToArray());
        }

        using var restarted = new MonitorRunRegistry(_dataPath);
        var run = restarted.Find(runId).ShouldNotBeNull();
        run.Finished.ShouldBeFalse();
        run.Orphaned.ShouldBeTrue();

        // The publisher outlived the monitor's restart after all — a live event un-orphans,
        // and the continued archive holds the full history.
        restarted.Record([new RunFinished(runId, 0, 3, 0, 1, 0, DateTimeOffset.UtcNow)]);
        run.Orphaned.ShouldBeFalse();
        run.Finished.ShouldBeTrue();
        File.ReadAllLines(restarted.ArchiveFileFor(runId)).Length.ShouldBe(realisticRun().Count);
    }

    [Fact]
    public void ejected_runs_do_not_rehydrate()
    {
        using (var first = new MonitorRunRegistry(_dataPath))
        {
            first.Record(realisticRun());
            first.Remove(runId);
        }

        using var restarted = new MonitorRunRegistry(_dataPath);
        restarted.Find(runId).ShouldBeNull();
        restarted.All().ShouldBeEmpty();
    }

    [Fact]
    public void a_torn_tail_line_does_not_sink_rehydration()
    {
        using (var first = new MonitorRunRegistry(_dataPath))
        {
            first.Record(realisticRun());
        }

        // Simulate the monitor being killed mid-write: a truncated final line.
        File.AppendAllText(Path.Combine(_dataPath, $"{runId}.ndjson"), "{\"type\":\"run_hea");

        using var restarted = new MonitorRunRegistry(_dataPath);
        restarted.Find(runId).ShouldNotBeNull().Scenarios.Count.ShouldBe(4);
    }

    [Fact]
    public void ctrf_reports_retries_flakiness_steps_and_pending_honestly()
    {
        var json = CtrfExport.Render(project());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("reportFormat").GetString().ShouldBe("CTRF");
        var results = root.GetProperty("results");

        var summary = results.GetProperty("summary");
        summary.GetProperty("tests").GetInt32().ShouldBe(4);
        summary.GetProperty("passed").GetInt32().ShouldBe(2);   // clean + pass-on-retry
        summary.GetProperty("failed").GetInt32().ShouldBe(1);
        summary.GetProperty("pending").GetInt32().ShouldBe(1);  // the lost scenario
        summary.GetProperty("flaky").GetInt32().ShouldBe(1);

        var tests = results.GetProperty("tests").EnumerateArray().ToArray();
        var flaky = tests.Single(t => t.GetProperty("name").GetString() == "Orders: broker");
        flaky.GetProperty("status").GetString().ShouldBe("passed");
        // suite is a hierarchy ARRAY in the spec, not a string — pinned after live schema
        // validation caught the original string form.
        flaky.GetProperty("suite")[0].GetString().ShouldBe("Orders");
        flaky.GetProperty("flaky").GetBoolean().ShouldBeTrue();
        flaky.GetProperty("retries").GetInt32().ShouldBe(1);
        flaky.GetProperty("extra").GetProperty("retryReasons")[0].GetString().ShouldBe("broker warms up slowly");

        // The full attempt history, spec-shaped: the retried-away attempt AND the final one
        // (the spec's own with-retries example ends its list with the passing attempt). Step
        // detail and the policy verdict ride each attempt's extra.
        var attempts = flaky.GetProperty("retryAttempts").EnumerateArray().ToArray();
        attempts.Length.ShouldBe(2);
        attempts[0].GetProperty("attempt").GetInt32().ShouldBe(1);
        attempts[0].GetProperty("status").GetString().ShouldBe("failed");
        attempts[0].GetProperty("message").GetString().ShouldBe("TimeoutException");
        attempts[0].GetProperty("extra").GetProperty("disposition").GetString().ShouldBe("RetryInProcess");
        attempts[0].GetProperty("extra").GetProperty("reason").GetString().ShouldBe("broker warms up slowly");
        attempts[0].GetProperty("extra").GetProperty("steps")[0].GetProperty("status").GetString().ShouldBe("failed");
        attempts[1].GetProperty("attempt").GetInt32().ShouldBe(2);
        attempts[1].GetProperty("status").GetString().ShouldBe("passed");
        attempts[1].GetProperty("extra").GetProperty("steps")[0].GetProperty("status").GetString().ShouldBe("passed");

        // A never-retried test carries no retryAttempts at all — not an empty array, and
        // never a null (CTRF's typed fields don't admit null).
        tests.Single(t => t.GetProperty("name").GetString() == "Calc: adds")
            .TryGetProperty("retryAttempts", out _).ShouldBeFalse();

        var failed = tests.Single(t => t.GetProperty("name").GetString() == "Calc: bad");
        failed.GetProperty("status").GetString().ShouldBe("failed");
        failed.GetProperty("steps")[0].GetProperty("status").GetString().ShouldBe("failed");

        results.GetProperty("extra").GetProperty("repository").GetString().ShouldBe("/repo");
    }

    [Fact]
    public void junit_maps_failures_errors_and_missing_outcomes_to_the_lossy_floor()
    {
        var xml = XDocument.Parse(JUnitExport.Render(project()));

        var suites = xml.Root.ShouldNotBeNull();
        suites.Attribute("tests")!.Value.ShouldBe("4");
        suites.Attribute("failures")!.Value.ShouldBe("1");

        var cases = suites.Descendants("testcase").ToArray();
        cases.Length.ShouldBe(4);

        cases.Single(c => c.Attribute("name")!.Value == "bad")
            .Element("failure")!.Attribute("message")!.Value.ShouldBe("expected 7 but was 8");

        cases.Single(c => c.Attribute("name")!.Value == "lost")
            .Element("skipped").ShouldNotBeNull();

        // The flaky pass is a plain green testcase here — retry detail is CTRF's job.
        cases.Single(c => c.Attribute("name")!.Value == "broker").HasElements.ShouldBeFalse();
    }
}
