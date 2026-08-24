using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issue #150 — a cancelled Run() throws and returns nothing, which is exactly what a capped
/// CI job looks like, and GitHub discards a cancelled job's logs on top of it. Snapshot() is
/// what survives: everything recorded, everything heard live, and an honest Indeterminate for
/// the rest.
/// </summary>
public class SnapshotTests
{
    private static readonly TimeSpan waitBudget = TimeSpan.FromSeconds(10);

    private sealed class Hold
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task.WaitAsync(waitBudget);
        public void Release() => _release.TrySetResult();

        public Task Enter()
        {
            _started.TrySetResult();
            return _release.Task;
        }
    }

    /// <summary>
    /// One class, so one partition, so one lane running the tests in this order — which is
    /// what makes "finished / in flight / not reached" deterministic mid-run.
    /// </summary>
    private static FakeWorkerFactory suite(Hold hold) => new()
    {
        Tests =
        [
            FakeWorkerFactory.Test("Suite/one"),
            FakeWorkerFactory.Test("Suite/bad"),
            FakeWorkerFactory.Test("Suite/hangs"),
            FakeWorkerFactory.Test("Suite/never")
        ],
        Outcome = (uid, _, _) => uid == "Suite/bad" ? WorkerTestState.Failed : WorkerTestState.Passed,
        HoldAfterStart = (uid, _) => uid == "Suite/hangs" ? hold.Enter() : Task.CompletedTask
    };

    [Fact]
    public async Task a_mid_run_snapshot_keeps_verdicts_heard_live_and_declares_the_rest()
    {
        var hold = new Hold();
        var supervisor = new Supervisor(suite(hold));

        var run = supervisor.Run();
        await hold.Started;

        var snapshot = supervisor.Snapshot();

        snapshot.IsPartial.ShouldBeTrue();
        snapshot.Tests.Count.ShouldBe(4);

        // Results are only recorded when a lane finishes, but the live stream already heard
        // these two verdicts — a snapshot must not call them indeterminate. (Indeterminate
        // tests classify as Failed too, as they always have, hence the filter.)
        snapshot.CleanPasses.ShouldHaveSingleItem().Uid.ShouldBe("Suite/one");
        snapshot.Failed.Where(t => !t.IsIndeterminate).ShouldHaveSingleItem().Uid.ShouldBe("Suite/bad");

        var hanging = snapshot.Tests.Single(t => t.Uid == "Suite/hangs");
        hanging.IsIndeterminate.ShouldBeTrue();
        hanging.Final.Outcome.ErrorMessage.ShouldContain("still executing");

        var unreached = snapshot.Tests.Single(t => t.Uid == "Suite/never");
        unreached.IsIndeterminate.ShouldBeTrue();
        unreached.Final.Outcome.ErrorMessage.ShouldContain("had not reached");

        // "We don't know" is exit code 2, and the report says the view is partial.
        snapshot.ExitCode.ShouldBe(2);
        snapshot.Summarize().ShouldContain("PARTIAL");
        RunReport.ToJson(snapshot).ShouldContain("\"partial\": true");

        // The snapshot changed nothing about the run itself.
        hold.Release();
        var results = await run.WaitAsync(waitBudget);
        results.IsPartial.ShouldBeFalse();
        results.Tests.Count.ShouldBe(4);
        results.CleanPasses.Count.ShouldBe(3);
        results.Failed.ShouldHaveSingleItem().Uid.ShouldBe("Suite/bad");
    }

    [Fact]
    public async Task a_cancelled_run_still_throws_but_the_snapshot_survives_it()
    {
        // The contract stays: Run(ct) throws on cancellation. The snapshot taken in the grace
        // period is what the consumer's ledger writer gets to keep.
        var hold = new Hold();
        var supervisor = new Supervisor(suite(hold));
        using var cancellation = new CancellationTokenSource();

        var run = supervisor.Run(cancellation.Token);
        await hold.Started;

        cancellation.Cancel();
        var snapshot = supervisor.Snapshot();

        hold.Release();
        await Should.ThrowAsync<OperationCanceledException>(async () => await run.WaitAsync(waitBudget));

        // Run() produced nothing — the snapshot is everything that survived, and it kept what
        // the run had learned.
        snapshot.IsPartial.ShouldBeTrue();
        snapshot.CleanPasses.ShouldHaveSingleItem().Uid.ShouldBe("Suite/one");
        snapshot.Failed.Where(t => !t.IsIndeterminate).ShouldHaveSingleItem().Uid.ShouldBe("Suite/bad");
        snapshot.Indeterminate.Count.ShouldBe(2);
    }

    [Fact]
    public void a_snapshot_before_any_run_is_empty_and_still_says_so_honestly()
    {
        var supervisor = new Supervisor(new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        });

        var snapshot = supervisor.Snapshot();

        snapshot.Tests.ShouldBeEmpty();
        snapshot.IsPartial.ShouldBeTrue();
    }

    [Fact]
    public async Task a_finished_runs_own_results_are_never_marked_partial()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var results = await new Supervisor(factory).Run().WaitAsync(waitBudget);

        results.IsPartial.ShouldBeFalse();
        results.Summarize().ShouldNotContain("PARTIAL");
        RunReport.ToJson(results).ShouldContain("\"partial\": false");
    }
}
