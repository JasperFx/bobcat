using System.Text.Json;
using Bobcat.Console.Contracts;
using Bobcat.Console.Mcp;
using Bobcat.Console.Runs;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// Issue #84, the server-side fold: the supervisor's lane topology, recycles and worker faults
/// folded into <see cref="RunProjection"/> from a recorded event sequence — and read back by MCP
/// <c>run_status</c>, <c>GET /api/runs/{id}</c>, and the CTRF export.
/// </summary>
/// <remarks>
/// These cases are a port of the dashboard's <c>runs-store-topology.test.ts</c>, event for event
/// and assertion for assertion, so the Pinia fold and this one cannot drift: a rule that changes
/// on one side fails the same-named case on the other. The replay-safety rules in particular are
/// shared by construction — a lane start no newer than the pass we are on, a finish older than
/// that pass, or a recycle/fault already seen is the archive being re-announced over live state.
/// </remarks>
public class SupervisorTopologyProjectionTests : IDisposable
{
    private static readonly Guid run = Guid.Parse("9a1f1a1e-0000-0000-0000-000000000084");

    private static DateTimeOffset at(string iso) => DateTimeOffset.Parse(iso);

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"bobcat-topology-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    private static RunStarted supervisedRunStarted()
        => new(run, "Wolverine PersistenceTests", "/Users/dev/code/wolverine", "main", "supervised",
            at("2026-08-21T10:00:00Z"), 3);

    private static ScenarioStarted scenarioStarted(string uid, string when)
    {
        var slash = uid.IndexOf('/');
        return new ScenarioStarted(run, uid, uid[..slash], uid[(slash + 1)..], 1, at(when));
    }

    private static ScenarioFinished scenarioFinished(string uid, string outcome)
        => new(run, uid, outcome, 1, 10, outcome == "CleanPass" ? null : "boom");

    /// <summary>The sequence a two-lane supervised run with one same-process retry and one dead worker produces.</summary>
    private static MonitorEvent[] recordedRun() =>
    [
        supervisedRunStarted(),
        new LaneStarted(run, 0, ["Orders/a", "Orders/b"], at("2026-08-21T10:00:01Z")),
        new LaneStarted(run, 1, ["Payments/c"], at("2026-08-21T10:00:01Z")),

        scenarioStarted("Orders/a", "2026-08-21T10:00:02Z"),
        scenarioStarted("Payments/c", "2026-08-21T10:00:02Z"),
        scenarioFinished("Orders/a", "Failed"),
        scenarioStarted("Orders/b", "2026-08-21T10:00:03Z"),
        scenarioFinished("Orders/b", "CleanPass"),
        new LaneFinished(run, 0, 2, false, at("2026-08-21T10:00:04Z")),

        // Lane 1's worker dies mid-test.
        new LaneFinished(run, 1, 0, true, at("2026-08-21T10:00:05Z")),
        new WorkerFaulted(run, 1,
            "the worker exited with code 139. Last standard error:\nSegmentation fault",
            139, "Segmentation fault", at("2026-08-21T10:00:05Z")),

        // Orders/a is retried in place: lane 0 starts again with only that test.
        new RetryScheduled(run, "Orders/a", 2, "RetryAfterRecycle", "the broker is slow to warm up"),
        new ResourceRecycled(run, "rabbit", at("2026-08-21T10:00:06Z")),
        new LaneStarted(run, 0, ["Orders/a"], at("2026-08-21T10:00:07Z")),
        scenarioStarted("Orders/a", "2026-08-21T10:00:08Z")
    ];

    private static RunProjection fold(params IEnumerable<MonitorEvent> events)
    {
        var projection = new RunProjection(run);
        foreach (var @event in events) projection.Apply(@event);
        return projection;
    }

    private static RunProjection playRecordedRun() => fold(recordedRun());

    private static string topologyOf(RunProjection projection)
        => JsonSerializer.Serialize(new { projection.Lanes, projection.Recycles, projection.WorkerFaults });

    [Fact]
    public void an_in_process_run_has_no_topology()
    {
        var projection = fold(new RunStarted(run, "Bobcat Acceptance", "/repo", null, "in-process",
            at("2026-08-21T10:00:00Z"), 1));

        projection.Lanes.ShouldBeEmpty();
        projection.Recycles.ShouldBeEmpty();
        projection.WorkerFaults.ShouldBeEmpty();
        projection.HasTopology.ShouldBeFalse();
    }

    [Fact]
    public void lanes_are_folded_in_lane_order_with_what_each_was_handed_and_what_it_is_running_now()
    {
        var projection = fold(
            supervisedRunStarted(),
            // Lane 1 announces first — arrival order is whatever the OS decided, lane order is not.
            new LaneStarted(run, 1, ["Payments/c"], at("2026-08-21T10:00:01Z")),
            new LaneStarted(run, 0, ["Orders/a", "Orders/b"], at("2026-08-21T10:00:01Z")),
            scenarioStarted("Orders/a", "2026-08-21T10:00:02Z"));

        projection.HasTopology.ShouldBeTrue();
        projection.Lanes.Select(l => l.Lane).ShouldBe([0, 1]);
        projection.Lanes[0].Uids.ShouldBe(["Orders/a", "Orders/b"]);
        projection.Lanes[0].Status.ShouldBe("running");
        projection.Lanes[0].Passes.ShouldBe(1);

        // "Running now" is the join of the lane's uids to live scenario state.
        projection.RunningIn(projection.Lanes[0]).Select(s => s.Uid).ShouldBe(["Orders/a"]);
        projection.RunningIn(projection.Lanes[1]).ShouldBeEmpty();

        projection.Apply(scenarioFinished("Orders/a", "CleanPass"));
        projection.RunningIn(projection.Lanes[0]).ShouldBeEmpty();
    }

    [Fact]
    public void a_lane_finishes_a_crashed_lane_says_so_and_the_worker_death_carries_its_exit_code_and_last_stderr()
    {
        var projection = playRecordedRun();

        var lane1 = projection.Lanes.Single(l => l.Lane == 1);
        lane1.Status.ShouldBe("crashed");
        lane1.FinishedAt.ShouldBe(at("2026-08-21T10:00:05Z"));
        lane1.Outcomes.ShouldBe(0);

        var fault = projection.WorkerFaults.ShouldHaveSingleItem();
        fault.Lane.ShouldBe(1);
        fault.ExitCode.ShouldBe(139);
        fault.StandardError.ShouldBe("Segmentation fault");
        fault.At.ShouldBe(at("2026-08-21T10:00:05Z"));
        fault.Fault.ShouldContain("exited with code 139");
    }

    [Fact]
    public void a_same_process_retry_starts_the_lane_again_as_a_second_pass_with_only_the_retried_test()
    {
        var projection = playRecordedRun();

        var lane0 = projection.Lanes.Single(l => l.Lane == 0);
        lane0.Passes.ShouldBe(2);
        lane0.Status.ShouldBe("running");
        lane0.Uids.ShouldBe(["Orders/a"]);
        lane0.FinishedAt.ShouldBeNull();
        lane0.Outcomes.ShouldBeNull();

        // And the retried scenario is what the lane is on, numbered by the scheduled attempt —
        // which means its first attempt's Failed outcome no longer reads as the scenario's
        // state: it is running again.
        projection.RunningIn(lane0).Select(s => (s.Uid, s.Attempt)).ShouldBe([("Orders/a", 2)]);
    }

    [Fact]
    public void recycles_are_a_timeline_in_order()
    {
        var projection = playRecordedRun();
        projection.Apply(new ResourceRecycled(run, "kafka", at("2026-08-21T10:00:06.5Z")));

        projection.Recycles.ShouldBe([
            new RecycleProjection("rabbit", at("2026-08-21T10:00:06Z")),
            new RecycleProjection("kafka", at("2026-08-21T10:00:06.5Z"))
        ]);
    }

    [Fact]
    public void replaying_the_archive_over_live_state_changes_nothing()
    {
        // Boot rehydration replays the NDJSON archive through the same Apply the live stream
        // fed, and the browser's hydrateFromServer does the same on its side — so every
        // topology handler must be idempotent, and an older lane start must never count as
        // another pass.
        var projection = playRecordedRun();
        var before = topologyOf(projection);

        foreach (var @event in recordedRun()) projection.Apply(@event);

        topologyOf(projection).ShouldBe(before);
        projection.Lanes.Count.ShouldBe(2);
        projection.Lanes.Single(l => l.Lane == 0).Passes.ShouldBe(2);
        projection.Lanes.Single(l => l.Lane == 1).Status.ShouldBe("crashed");
        projection.Recycles.Count.ShouldBe(1);
        projection.WorkerFaults.Count.ShouldBe(1);
    }

    [Fact]
    public void a_replayed_finish_from_an_earlier_pass_does_not_close_the_pass_the_lane_is_on()
    {
        var projection = playRecordedRun();
        // The archive's first-pass finish for lane 0 arrives after the live second-pass start.
        projection.Apply(new LaneFinished(run, 0, 2, false, at("2026-08-21T10:00:04Z")));

        var lane0 = projection.Lanes.Single(l => l.Lane == 0);
        lane0.Status.ShouldBe("running");
        lane0.Passes.ShouldBe(2);
    }

    [Fact]
    public void tolerates_topology_events_arriving_before_run_started_and_a_finish_without_its_start()
    {
        var projection = fold(
            new LaneFinished(run, 3, 4, false, at("2026-08-21T10:00:09Z")),
            new WorkerFaulted(run, null, "the worker stopped responding but is still running", null, null,
                at("2026-08-21T10:00:10Z")));

        var lane = projection.Lanes.ShouldHaveSingleItem();
        lane.Lane.ShouldBe(3);
        lane.Status.ShouldBe("finished");
        lane.Passes.ShouldBe(1);
        lane.Outcomes.ShouldBe(4);

        var fault = projection.WorkerFaults.ShouldHaveSingleItem();
        fault.Lane.ShouldBeNull();
        fault.ExitCode.ShouldBeNull();
        fault.StandardError.ShouldBeNull();

        // Metadata backfills when run_started shows up late.
        projection.Apply(supervisedRunStarted());
        projection.Suite.ShouldBe("Wolverine PersistenceTests");
        projection.Lanes.Count.ShouldBe(1);
    }

    [Fact]
    public void a_restarted_registry_rehydrates_the_topology_and_eject_drops_it_with_the_run()
    {
        // The server's analog of the store's "eject drops the topology": the registry is where
        // the projection lives, and its archive is what survives a restart.
        using (var first = new MonitorRunRegistry(_dataPath))
        {
            first.Record(recordedRun());
        }

        using var restarted = new MonitorRunRegistry(_dataPath);
        var rehydrated = restarted.Find(run).ShouldNotBeNull();
        topologyOf(rehydrated).ShouldBe(topologyOf(playRecordedRun()));

        restarted.Remove(run).ShouldBeTrue();
        restarted.Find(run).ShouldBeNull();
    }

    // ---- the read sides ----

    private MonitorRunRegistry recorded()
    {
        var registry = new MonitorRunRegistry(_dataPath);
        registry.Record(recordedRun());
        return registry;
    }

    [Fact]
    public void run_status_reports_lanes_recycles_and_worker_faults()
    {
        using var registry = recorded();

        var status = JsonDocument.Parse(MonitorTools.RunStatus(registry, run.ToString())).RootElement;

        var lanes = status.GetProperty("lanes").EnumerateArray().ToArray();
        lanes.Select(l => l.GetProperty("lane").GetInt32()).ShouldBe([0, 1]);
        lanes[0].GetProperty("status").GetString().ShouldBe("running");
        lanes[0].GetProperty("passes").GetInt32().ShouldBe(2);
        lanes[0].GetProperty("uids").EnumerateArray().Select(u => u.GetString()).ShouldBe(["Orders/a"]);
        lanes[0].GetProperty("running").EnumerateArray().Select(u => u.GetString()).ShouldBe(["Orders/a"]);
        lanes[1].GetProperty("status").GetString().ShouldBe("crashed");
        lanes[1].GetProperty("outcomes").GetInt32().ShouldBe(0);
        // The crashed worker's scenario never reported an outcome, and the fold infers none
        // for it — it is still "running" as far as the scenario stream knows, which is exactly
        // the signal that it was lost with the worker (RunFinished counts it Indeterminate).
        lanes[1].GetProperty("running").EnumerateArray().Select(u => u.GetString()).ShouldBe(["Payments/c"]);

        var recycle = status.GetProperty("recycles").EnumerateArray().ShouldHaveSingleItem();
        recycle.GetProperty("resource").GetString().ShouldBe("rabbit");

        var fault = status.GetProperty("workerFaults").EnumerateArray().ShouldHaveSingleItem();
        fault.GetProperty("lane").GetInt32().ShouldBe(1);
        fault.GetProperty("exitCode").GetInt32().ShouldBe(139);
        fault.GetProperty("standardError").GetString().ShouldBe("Segmentation fault");
        fault.GetProperty("fault").GetString().ShouldContain("exited with code 139");

        // The retried scenario reads as running again, not as its first attempt's failure.
        status.GetProperty("scenarios").EnumerateArray()
            .Single(s => s.GetProperty("uid").GetString() == "Orders/a")
            .GetProperty("status").GetString().ShouldBe("running");
    }

    [Fact]
    public void run_status_of_an_in_process_run_has_empty_topology_arrays_not_missing_ones()
    {
        // An agent should never have to guess whether the field is absent or the run simply had
        // no lanes.
        using var registry = new MonitorRunRegistry(_dataPath);
        registry.Record([new RunStarted(run, "Bobcat Acceptance", "/repo", null, "in-process",
            at("2026-08-21T10:00:00Z"), 1)]);

        var status = JsonDocument.Parse(MonitorTools.RunStatus(registry, run.ToString())).RootElement;
        status.GetProperty("lanes").GetArrayLength().ShouldBe(0);
        status.GetProperty("recycles").GetArrayLength().ShouldBe(0);
        status.GetProperty("workerFaults").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void the_run_detail_endpoint_exposes_the_same_topology()
    {
        using var registry = recorded();

        var detail = RunEndpoints.Find(run, registry).ShouldBeOfType<Ok<RunDetail>>().Value.ShouldNotBeNull();

        detail.Lanes.Select(l => l.Lane).ShouldBe([0, 1]);
        var lane0 = detail.Lanes[0];
        lane0.Status.ShouldBe("running");
        lane0.Passes.ShouldBe(2);
        lane0.Uids.ShouldBe(["Orders/a"]);
        lane0.Running.ShouldBe(["Orders/a"]);
        lane0.StartedAt.ShouldBe(at("2026-08-21T10:00:07Z"));
        lane0.FinishedAt.ShouldBeNull();
        lane0.Outcomes.ShouldBeNull();
        detail.Lanes[1].Status.ShouldBe("crashed");
        detail.Lanes[1].Running.ShouldBe(["Payments/c"]); // lost with the worker, never finished

        detail.Recycles.ShouldBe([new RecycleResult("rabbit", at("2026-08-21T10:00:06Z"))]);

        var fault = detail.WorkerFaults.ShouldHaveSingleItem();
        fault.Lane.ShouldBe(1);
        fault.ExitCode.ShouldBe(139);
        fault.StandardError.ShouldBe("Segmentation fault");
    }

    [Fact]
    public void ctrf_carries_faults_recycles_and_lanes_in_the_results_extra_block()
    {
        var json = CtrfExport.Render(playRecordedRun());
        using var doc = JsonDocument.Parse(json);
        var extra = doc.RootElement.GetProperty("results").GetProperty("extra");

        var faults = extra.GetProperty("workerFaults").EnumerateArray().ToArray();
        var fault = faults.ShouldHaveSingleItem();
        fault.GetProperty("lane").GetInt32().ShouldBe(1);
        fault.GetProperty("exitCode").GetInt32().ShouldBe(139);
        fault.GetProperty("standardError").GetString().ShouldBe("Segmentation fault");

        extra.GetProperty("recycles")[0].GetProperty("resource").GetString().ShouldBe("rabbit");

        var lanes = extra.GetProperty("lanes").EnumerateArray().ToArray();
        lanes.Length.ShouldBe(2);
        lanes[0].GetProperty("passes").GetInt32().ShouldBe(2);
        lanes[1].GetProperty("status").GetString().ShouldBe("crashed");

        // Nothing new at the top level of the report: CTRF has no vocabulary for lanes or
        // worker processes, and the schema would reject an invented field there.
        doc.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["reportFormat", "specVersion", "results"]);
        doc.RootElement.GetProperty("results").EnumerateObject().Select(p => p.Name)
            .ShouldBe(["tool", "summary", "tests", "extra"]);
    }

    [Fact]
    public void ctrf_of_an_in_process_run_omits_the_topology_fields_entirely()
    {
        // Same rule as retryAttempts: omitted, not null and not an empty array — an in-process
        // export is byte-identical to what it was before the fold existed.
        var json = CtrfExport.Render(fold(new RunStarted(run, "Bobcat Acceptance", "/repo", null, "in-process",
            at("2026-08-21T10:00:00Z"), 1)));
        using var doc = JsonDocument.Parse(json);
        var extra = doc.RootElement.GetProperty("results").GetProperty("extra");

        extra.TryGetProperty("lanes", out _).ShouldBeFalse();
        extra.TryGetProperty("recycles", out _).ShouldBeFalse();
        extra.TryGetProperty("workerFaults", out _).ShouldBeFalse();
    }
}
