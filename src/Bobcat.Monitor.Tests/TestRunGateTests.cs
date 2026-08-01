using Bobcat.Monitor.Contracts;
using Bobcat.Monitor.Coordination;
using Bobcat.Monitor.Coordination.GitHub;
using Bobcat.Monitor.Coordination.NuGet;
using Bobcat.Monitor.Runs;
using Shouldly;

namespace Bobcat.Monitor.Tests;

public class TestRunGateTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bobcat-gate-tests", Guid.NewGuid().ToString("N"));

    private readonly MonitorRunRegistry _runs;

    public TestRunGateTests() => _runs = new MonitorRunRegistry(Path.Combine(_root, "runs"));

    public void Dispose()
    {
        _runs.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private const string GatePlan =
        """
        schema: 1
        plan: epic
        nodes:
          - id: build-it
            kind: issue
            repo: JasperFx/bobcat
            issue: 1
          - id: suite-green
            kind: test-run-gate
            depends_on: [build-it]
        """;

    private PlanStatusView status()
    {
        var result = PlanParser.Parse(GatePlan);
        result.Errors.ShouldBeEmpty();
        var plan = new RegisteredPlan("epic", PlanSource.Pushed, result.Document, null, DateTimeOffset.UtcNow, []);

        var gitHub = new GitHubStatusCache();
        gitHub.Upsert(new GitHubObservation(
            "JasperFx/bobcat#1", "issue", "closed", "t", [], [], [], false, DateTimeOffset.UtcNow));

        return PlanStatus.For(plan, new ObservationStores(
            gitHub, new PackagePinCache(), new NuGetStatusCache(),
            new NuGetBaselineStore(Path.Combine(_root, "data")), _runs));
    }

    private Guid startRun(string? planNode, DateTimeOffset? at = null, int? total = 3)
    {
        var runId = Guid.NewGuid();
        _runs.Record([new RunStarted(
            runId, "MySpecs", "/repo", "main", "in-process",
            at ?? DateTimeOffset.UtcNow, total, planNode)]);
        return runId;
    }

    private void finishRun(Guid runId, int exitCode, int passed, int failed, int passedOnRetry = 0, int indeterminate = 0)
        => _runs.Record([new RunFinished(
            runId, exitCode, passed, failed, passedOnRetry, indeterminate, DateTimeOffset.UtcNow)]);

    [Fact]
    public void a_gate_with_no_correlated_run_waits_and_says_how_to_correlate()
    {
        startRun(planNode: null); // an uncorrelated run on the same box is nobody's evidence

        var gate = status().Nodes.Single(x => x.Id == "suite-green");
        gate.Status.ShouldBe("waiting");
        gate.Detail.ShouldContain("BOBCAT_PLAN_NODE=epic/suite-green");
        gate.RunId.ShouldBeNull();
        gate.Ready.ShouldBeTrue(); // its dependency is done — launching the suite is the work
    }

    [Fact]
    public void a_running_suite_renders_running_with_progress_and_is_not_ready()
    {
        var runId = startRun("epic/suite-green");
        _runs.Record([
            new ScenarioStarted(runId, "F/one", "F", "one", 1, DateTimeOffset.UtcNow),
            new ScenarioFinished(runId, "F/one", "CleanPass", 1, 5, null)
        ]);

        var gate = status().Nodes.Single(x => x.Id == "suite-green");
        gate.Status.ShouldBe("running");
        gate.Detail.ShouldBe("MySpecs running — 1 of 3 scenarios finished");
        gate.RunId.ShouldBe(runId);
        gate.Ready.ShouldBeFalse(); // in flight — dispatching another run is not the move
    }

    [Fact]
    public void exit_zero_is_done_and_a_retried_pass_stays_visible()
    {
        var runId = startRun("epic/suite-green");
        finishRun(runId, exitCode: 0, passed: 2, failed: 0, passedOnRetry: 1);

        var gate = status().Nodes.Single(x => x.Id == "suite-green");
        gate.Status.ShouldBe("done");
        gate.Detail.ShouldBe("MySpecs passed (2 clean, 1 on retry)");
        gate.RunId.ShouldBe(runId);
    }

    [Fact]
    public void a_failed_run_fails_the_gate_and_stays_ready_to_rerun()
    {
        var runId = startRun("epic/suite-green");
        finishRun(runId, exitCode: 2, passed: 1, failed: 1, indeterminate: 1);

        var gate = status().Nodes.Single(x => x.Id == "suite-green");
        gate.Status.ShouldBe("failed");
        gate.Detail.ShouldBe("MySpecs failed (1 failed, 1 indeterminate, exit 2)");
        gate.Ready.ShouldBeTrue(); // re-running the suite is the action
    }

    [Fact]
    public void the_latest_correlated_run_is_the_verdict()
    {
        var earlier = startRun("epic/suite-green", DateTimeOffset.UtcNow.AddMinutes(-10));
        finishRun(earlier, exitCode: 1, passed: 1, failed: 2);

        var rerun = startRun("epic/suite-green");
        finishRun(rerun, exitCode: 0, passed: 3, failed: 0);

        var gate = status().Nodes.Single(x => x.Id == "suite-green");
        gate.Status.ShouldBe("done"); // gates re-run; the current evidence is what counts
        gate.RunId.ShouldBe(rerun);
    }

    [Fact]
    public void an_orphaned_run_proves_nothing_and_fails_the_gate()
    {
        // Orphaning the real way: a monitor restart rehydrates an archive whose publisher
        // died before posting RunFinished.
        startRun("epic/suite-green");
        _runs.Dispose();
        using var restarted = new MonitorRunRegistry(Path.Combine(_root, "runs"));

        var result = PlanParser.Parse(GatePlan);
        var plan = new RegisteredPlan("epic", PlanSource.Pushed, result.Document, null, DateTimeOffset.UtcNow, []);
        var view = PlanStatus.For(plan, new ObservationStores(
            new GitHubStatusCache(), new PackagePinCache(), new NuGetStatusCache(),
            new NuGetBaselineStore(Path.Combine(_root, "data2")), restarted));

        var gate = view.Nodes.Single(x => x.Id == "suite-green");
        gate.Status.ShouldBe("failed");
        gate.Detail.ShouldContain("orphaned");
    }
}
