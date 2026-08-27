using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issue #173 — acting on a stall, the deferred second half of #145's detection. Same fake-clock
/// discipline as <see cref="StallAndHeartbeatTests"/>: a hung test is a worker held on a
/// TaskCompletionSource, a kill breaks the hold the way killing a real process would, and time
/// only moves when the test says so. The default (<see cref="StallAction.Report"/>) is pinned by
/// the existing stall tests, which release their holds by hand and finish green.
/// </summary>
public class StallActionTests
{
    private static readonly TimeSpan waitBudget = TimeSpan.FromSeconds(10);

    private sealed class Hold
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task.WaitAsync(waitBudget);

        public Task Enter()
        {
            _started.TrySetResult();
            return _release.Task;
        }
    }

    private sealed class RecordingObserver : ISupervisorObserver
    {
        public List<StallKill> Kills { get; } = [];
        public List<(string Uid, int Attempt)> RetriesScheduled { get; } = [];

        public void StallKilled(StallKill kill)
        {
            lock (Kills) Kills.Add(kill);
        }

        public void RetryScheduled(string uid, int nextAttempt, Bobcat.Resilience.Disposition disposition)
        {
            lock (RetriesScheduled) RetriesScheduled.Add((uid, nextAttempt));
        }
    }

    [Fact]
    public async Task kill_and_retry_clears_the_wedge_and_resumes_the_innocent_batch_mates()
    {
        var time = new FakeTimeProvider();
        var observer = new RecordingObserver();
        var hold = new Hold();

        var factory = new FakeWorkerFactory
        {
            Tests =
            [
                FakeWorkerFactory.InClass("Suite", "first"),
                FakeWorkerFactory.InClass("Suite", "hangs"),
                FakeWorkerFactory.InClass("Suite", "last")
            ],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            // Held only in the lane worker: the solo stall retry runs clean, modelling the
            // environmental wedge the consumer case describes.
            HoldAfterStart = (uid, worker) =>
                uid == "Suite.hangs" && worker.Launch.Purpose == WorkerPurpose.Lane
                    ? hold.Enter()
                    : Task.CompletedTask
        };

        // Deliberately NO RetryBudget: a stall kill rides its own count, so the retries below
        // happening at all proves the operator's budget was never consulted.
        var supervisor = new Supervisor(factory)
        {
            Time = time,
            StallThreshold = TimeSpan.FromSeconds(30),
            StallAction = StallAction.KillAndRetry
        };
        supervisor.AddObserver(observer);

        var run = supervisor.Run();
        await hold.Started;
        time.Advance(TimeSpan.FromSeconds(31));

        var results = await run.WaitAsync(waitBudget);

        // Everything green in the end: the wedged test passed alone, and the batch-mate killed
        // alongside it resumed in its lane instead of being reported indeterminate.
        results.ExitCode.ShouldBe(0);
        results.Tests.ShouldAllBe(t => t.Final.Succeeded);

        var kill = results.StallKills.ShouldHaveSingleItem();
        kill.Uid.ShouldBe("Suite.hangs");
        kill.Lane.ShouldBe(0);
        observer.Kills.ShouldHaveSingleItem().Uid.ShouldBe("Suite.hangs");

        // The killed attempts are stall-induced, so the flakiness surfaces stay clean: a wedge
        // is not a flake, and nothing here was ever unreliable on its own account.
        results.PassedOnRetry.ShouldBeEmpty();
        results.Quarantine.ShouldBeEmpty();
        results.Tests.Single(t => t.Uid == "Suite.hangs").Outcome.ShouldBe(Bobcat.Resilience.RunOutcome.CleanPass);

        // And the kill was the supervisor's own act, not a worker fault.
        results.WorkerFaults.ShouldBeEmpty();
        results.StalledTests.ShouldHaveSingleItem();

        // The wedged test retried alone; the innocent one resumed in its lane.
        var hangs = results.Tests.Single(t => t.Uid == "Suite.hangs");
        hangs.Attempts[0].StallInduced.ShouldBeTrue();
        hangs.Attempts[0].Outcome.State.ShouldBe(WorkerTestState.Indeterminate);
        hangs.Attempts[1].Placement.ShouldBe(AttemptPlacement.IsolatedProcess);

        var last = results.Tests.Single(t => t.Uid == "Suite.last");
        last.Attempts[0].StallInduced.ShouldBeTrue();
        last.Attempts[1].Placement.ShouldBe(AttemptPlacement.SameProcess);

        // The one that finished before the wedge is untouched.
        results.Tests.Single(t => t.Uid == "Suite.first").AttemptCount.ShouldBe(1);

        var report = RunReport.ToText(results);
        report.ShouldContain("Workers killed to clear a stall");
        RunReport.ToJson(results).ShouldContain("stallKills");
    }

    [Fact]
    public async Task a_test_that_stalls_again_on_its_solo_retry_is_failed_not_retried_forever()
    {
        var time = new FakeTimeProvider();
        var first = new Hold();
        var second = new Hold();
        var entered = 0;

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Suite/hangs")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (uid, _) => uid == "Suite/hangs"
                ? (Interlocked.Increment(ref entered) == 1 ? first : second).Enter()
                : Task.CompletedTask
        };

        var supervisor = new Supervisor(factory)
        {
            Time = time,
            StallThreshold = TimeSpan.FromSeconds(30),
            StallAction = StallAction.KillAndRetry
        };

        var run = supervisor.Run();

        await first.Started;
        time.Advance(TimeSpan.FromSeconds(31));

        // The solo retry wedges too — a deterministic hang, or infrastructure that is simply
        // dead. A new attempt is a new stall clock, so the second crossing fires as well.
        await second.Started;
        time.Advance(TimeSpan.FromSeconds(31));

        var results = await run.WaitAsync(waitBudget);

        var report = results.Tests.ShouldHaveSingleItem();
        report.AttemptCount.ShouldBe(2);
        report.Attempts.ShouldAllBe(a => a.StallInduced);
        report.Outcome.ShouldBe(Bobcat.Resilience.RunOutcome.Failed);
        report.Final.Disposition.Reason.ShouldContain("stalled again");

        results.StallKills.Count.ShouldBe(2);
        // Its verdict was never established, and that is exit 2's meaning.
        results.ExitCode.ShouldBe(2);
    }

    [Fact]
    public async Task abort_run_stops_on_the_first_stall_and_names_the_test()
    {
        var time = new FakeTimeProvider();
        var hold = new Hold();

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Suite/hangs"), FakeWorkerFactory.Test("Suite/after")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (uid, _) => uid == "Suite/hangs" ? hold.Enter() : Task.CompletedTask
        };

        var supervisor = new Supervisor(factory)
        {
            Time = time,
            StallThreshold = TimeSpan.FromSeconds(30),
            StallAction = StallAction.AbortRun
        };

        var run = supervisor.Run();
        await hold.Started;
        time.Advance(TimeSpan.FromSeconds(31));

        var results = await run.WaitAsync(waitBudget);

        results.AbortReason.ShouldNotBeNull();
        results.AbortReason.ShouldContain("Suite/hangs");
        results.AbortReason.ShouldContain("AbortRun");
        results.ExitCode.ShouldBe(2);

        // Nothing was killed FOR A RETRY — the abort is its own path, not a kill ledger entry.
        results.StallKills.ShouldBeEmpty();
    }

    [Fact]
    public async Task repeated_stalls_across_tests_exhaust_the_kill_ceiling_and_abort()
    {
        var time = new FakeTimeProvider();
        var holdA = new Hold();
        var holdB = new Hold();

        var factory = new FakeWorkerFactory
        {
            Tests =
            [
                FakeWorkerFactory.InClass("ClassA", "hangs"),
                FakeWorkerFactory.InClass("ClassB", "hangs")
            ],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (uid, worker) => worker.Launch.Purpose == WorkerPurpose.Lane
                ? (uid.StartsWith("ClassA") ? holdA : holdB).Enter()
                : Task.CompletedTask
        };

        var supervisor = new Supervisor(factory)
        {
            Time = time,
            MaxParallelWorkers = 2,
            StallThreshold = TimeSpan.FromSeconds(30),
            StallAction = StallAction.KillAndRetry,
            // The environment-died shape: after one kill, a second stall is not a coincidence.
            MaxStallKills = 1
        };

        var run = supervisor.Run();
        await holdA.Started;
        await holdB.Started;
        time.Advance(TimeSpan.FromSeconds(31));

        var results = await run.WaitAsync(waitBudget);

        results.AbortReason.ShouldNotBeNull();
        results.AbortReason.ShouldContain("stall kill");
        results.StallKills.Count.ShouldBe(1);
        results.ExitCode.ShouldBe(2);
    }
}
