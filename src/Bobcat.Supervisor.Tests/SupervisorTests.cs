using Bobcat.Engine;
using Bobcat.Resilience;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

public class SupervisorTests
{
    private static Supervisor build(FakeWorkerFactory factory, RetryBudget? budget = null)
        => new(factory) { RetryBudget = budget ?? RetryBudget.None };

    // ---------------------------------------------------------------- scheduling

    [Fact]
    public async Task an_isolated_test_is_kept_out_of_the_batch_and_given_its_own_worker()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a"), FakeWorkerFactory.Test("b"),
                     FakeWorkerFactory.Test("lonely", "Isolated=true")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await build(factory).Run();

        var runs = factory.RunningWorkers;
        runs.Count.ShouldBe(2);

        // The batch never contains the isolated test...
        runs[0].Runs.Single().ShouldBe(["a", "b"]);
        // ...and the isolated test gets a process to itself.
        runs[1].Runs.Single().ShouldBe(["lonely"]);
    }

    [Fact]
    public async Task discovery_uses_a_worker_that_never_runs_anything()
    {
        // Discovery must not inherit state from, or leave state in, a process that goes on to
        // execute tests.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await build(factory).Run();

        factory.Launched[0].Runs.ShouldBeEmpty();
        factory.Launched[0].Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task every_isolated_test_gets_a_separate_process()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("x", "Isolated=true"), FakeWorkerFactory.Test("y", "Isolated=true")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var results = await build(factory).Run();

        factory.RunningWorkers.Count.ShouldBe(2);
        // Isolation is not free, and the report says how much it cost.
        results.WorkersLaunched.ShouldBe(3); // 1 discovery + 2 isolated
    }

    // ---------------------------------------------------------------- filtering

    [Fact]
    public async Task a_filtered_out_test_is_never_run_and_never_reported()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("keep"), FakeWorkerFactory.Test("quarantined", "Category=Flaky")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = build(factory);
        supervisor.TestFilter = t => !t.Traits.TryGetValue("Category", out var category) || category != "Flaky";

        var results = await supervisor.Run();

        // Excluded before scheduling: no worker was ever asked for it...
        factory.RunningWorkers.SelectMany(w => w.Runs).ShouldAllBe(uids => !uids!.Contains("quarantined"));
        // ...and the report does not mention it, even as a skip.
        results.Tests.Select(t => t.Uid).ShouldBe(["keep"]);
    }

    [Fact]
    public async Task a_filter_keeping_nothing_yields_an_empty_clean_run()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = build(factory);
        supervisor.TestFilter = _ => false;

        var results = await supervisor.Run();

        results.Tests.ShouldBeEmpty();
        // Only the discovery worker ran. Deciding whether "nothing matched" is an error belongs
        // to the caller, who knows whether the filter was supposed to match anything.
        factory.RunningWorkers.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- releasing idle lanes

    [Fact]
    public async Task idle_lanes_are_released_before_a_fresh_process_retry_when_asked()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a"), FakeWorkerFactory.Test("flaky", "Isolated=false")],
            Outcome = (uid, attempt, _) =>
                uid == "flaky" && attempt == 1 ? WorkerTestState.Failed : WorkerTestState.Passed
        };

        var supervisor = build(factory, new RetryBudget { MaxAttemptsPerTest = 2 });
        supervisor.ReleaseIdleLanes = true;
        supervisor.AddFailurePolicy(new AlwaysFreshProcessPolicy());

        var results = await supervisor.Run();

        results.PassedOnRetry.Single().DisplayName.ShouldBe("flaky");

        // The batch lane was disposed before the retry worker was launched — the whole point is
        // never holding workers+1 processes at once.
        var batchLane = factory.RunningWorkers.First(w => w.Runs.Any(r => r!.Contains("a")));
        var retryWorker = factory.RunningWorkers.Last();
        batchLane.Disposed.ShouldBeTrue();
        retryWorker.ShouldNotBeSameAs(batchLane);
    }

    [Fact]
    public async Task an_in_process_retry_decided_after_the_release_is_unsupported_not_silently_rerun()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("stubborn")],
            Outcome = (_, _, _) => WorkerTestState.Failed
        };

        var supervisor = build(factory, new RetryBudget { MaxAttemptsPerTest = 3 });
        supervisor.ReleaseIdleLanes = true;
        // Fresh process first, then asks for the lane it came from — which no longer exists.
        supervisor.AddFailurePolicy(new FreshThenInProcessPolicy());

        var results = await supervisor.Run();

        var test = results.Tests.Single();
        test.AttemptCount.ShouldBe(2);
        test.Final.Unsupported.ShouldNotBeNull();
        test.Final.Unsupported.ShouldContain("NOT retried");
    }

    private class AlwaysFreshProcessPolicy : IFailurePolicy
    {
        public Disposition Decide(AttemptContext attempt)
            => attempt.Succeeded || !attempt.RetriesAvailable
                ? null
                : Disposition.RetryInFreshProcess("fresh, always");
    }

    private class FreshThenInProcessPolicy : IFailurePolicy
    {
        public Disposition Decide(AttemptContext attempt)
        {
            if (attempt.Succeeded || !attempt.RetriesAvailable) return null;
            return attempt.AttemptNumber == 1
                ? Disposition.RetryInFreshProcess("fresh first")
                : Disposition.RetryInProcess("then warm, too late");
        }
    }

    // ---------------------------------------------------------------- retries

    [Fact]
    public async Task a_same_process_retry_reuses_the_shared_worker()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("flaky", "Retry=2")],
            Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Failed
        };

        var results = await build(factory, new RetryBudget { MaxAttemptsPerTest = 2 }).Run();

        var runner = factory.RunningWorkers.ShouldHaveSingleItem();
        runner.Runs.Count.ShouldBe(2); // both attempts on the same worker

        var test = results.Tests.ShouldHaveSingleItem();
        test.Outcome.ShouldBe(RunOutcome.PassOnRetry);
        test.Attempts[1].Placement.ShouldBe(AttemptPlacement.SameProcess);
    }

    [Fact]
    public async Task a_fresh_process_retry_launches_a_new_worker_and_runs_the_test_alone()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("needs_air", "Retry=2", "Isolated=true")],
            Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Failed
        };

        var results = await build(factory, new RetryBudget { MaxAttemptsPerTest = 2 }).Run();

        // Two separate running workers, each asked for exactly this one test.
        var runners = factory.RunningWorkers;
        runners.Count.ShouldBe(2);
        runners.ShouldAllBe(w => w.Runs.Count == 1);

        var test = results.Tests.ShouldHaveSingleItem();
        test.Outcome.ShouldBe(RunOutcome.PassOnRetry);
        test.Attempts.ShouldAllBe(a => a.Placement == AttemptPlacement.IsolatedProcess);
    }

    [Fact]
    public async Task retries_stop_at_the_budget()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("hopeless", "Retry=5")],
            Outcome = (_, _, _) => WorkerTestState.Failed
        };

        var results = await build(factory, new RetryBudget { MaxAttemptsPerTest = 3 }).Run();

        var test = results.Tests.ShouldHaveSingleItem();
        test.AttemptCount.ShouldBe(3);
        test.Outcome.ShouldBe(RunOutcome.Failed);
        results.ExitCode.ShouldBe(1);
    }

    [Fact]
    public async Task an_untagged_failure_is_never_retried()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("plain")],
            Outcome = (_, _, _) => WorkerTestState.Failed
        };

        var results = await build(factory, new RetryBudget { MaxAttemptsPerTest = 5 }).Run();

        results.Tests.ShouldHaveSingleItem().AttemptCount.ShouldBe(1);
        results.RetriesPerformed.ShouldBe(0);
    }

    // ---------------------------------------------------------------- honest reporting

    [Fact]
    public async Task a_pass_on_retry_is_reported_separately_from_a_clean_pass()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("solid"), FakeWorkerFactory.Test("flaky", "Retry=2")],
            Outcome = (uid, attempt, _) =>
                uid == "solid" || attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Failed
        };

        var results = await build(factory, new RetryBudget { MaxAttemptsPerTest = 2 }).Run();

        results.CleanPasses.ShouldHaveSingleItem().Uid.ShouldBe("solid");
        results.PassedOnRetry.ShouldHaveSingleItem().Uid.ShouldBe("flaky");

        // Green build, but the flakiness is on the record rather than folded into the passes.
        results.ExitCode.ShouldBe(0);
        results.Summarize().ShouldContain("1 passed on retry");
    }

    // ---------------------------------------------------------------- recycling

    [Fact]
    public async Task a_recycle_disposition_throws_the_resource_away_before_retrying()
    {
        var rabbit = new FakeRecyclableResource("rabbit");

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("brokered", "RecycleOnRetry=rabbit", "Retry=2")],
            Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Failed
        };

        var supervisor = build(factory, new RetryBudget { MaxAttemptsPerTest = 2 });
        supervisor.AddRecyclableResource(rabbit);

        var results = await supervisor.Run();

        rabbit.RecycleCount.ShouldBe(1);

        var test = results.Tests.ShouldHaveSingleItem();
        test.Outcome.ShouldBe(RunOutcome.PassOnRetry);

        // Recycled, then run alone in a fresh process — reusing the shared worker would leave it
        // connected to the broker we just discarded.
        test.Attempts[1].Placement.ShouldBe(AttemptPlacement.RecycledProcess);
        results.Recyclings.ShouldBe(["rabbit"]);
        results.Summarize().ShouldContain("Recycled: rabbit");
    }

    [Fact]
    public async Task the_recycle_happens_before_the_retry_runs_not_after()
    {
        // Ordering is the whole point: a retry against the old broker proves nothing.
        var order = new List<string>();
        var rabbit = new FakeRecyclableResource("rabbit", () => order.Add("recycle"));

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("brokered", "RecycleOnRetry=rabbit", "Retry=2")],
            Outcome = (_, attempt, _) =>
            {
                order.Add($"run{attempt}");
                return attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Failed;
            }
        };

        var supervisor = build(factory, new RetryBudget { MaxAttemptsPerTest = 2 });
        supervisor.AddRecyclableResource(rabbit);

        await supervisor.Run();

        order.ShouldBe(["run1", "recycle", "run2"]);
    }

    [Fact]
    public async Task naming_a_resource_nobody_registered_is_reported_not_silently_ignored()
    {
        // A wiring mistake. Retrying without recycling would hide it behind an ordinary flaky
        // failure.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("brokered", "RecycleOnRetry=kafka", "Retry=2")],
            Outcome = (_, _, _) => WorkerTestState.Failed
        };

        var results = await build(factory, new RetryBudget { MaxAttemptsPerTest = 2 }).Run();

        var test = results.Tests.ShouldHaveSingleItem();
        test.AttemptCount.ShouldBe(1);

        var unsupported = test.UnsupportedDispositions.ShouldHaveSingleItem();
        unsupported.ShouldContain("kafka");
        unsupported.ShouldContain("not registered");
        unsupported.ShouldContain("AddRecyclableResource");
    }

    [Fact]
    public async Task a_failed_recycle_aborts_rather_than_retrying_against_unknown_infrastructure()
    {
        var rabbit = new FakeRecyclableResource("rabbit",
            () => throw new InvalidOperationException("docker is not responding"));

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("brokered", "RecycleOnRetry=rabbit", "Retry=2")],
            Outcome = (_, _, _) => WorkerTestState.Failed
        };

        var supervisor = build(factory, new RetryBudget { MaxAttemptsPerTest = 2 });
        supervisor.AddRecyclableResource(rabbit);

        var results = await supervisor.Run();

        // Aborted rather than thrown, so everything already learned this run survives — a
        // thrown exception would discard the results collected before the infrastructure broke.
        results.AbortReason.ShouldNotBeNull();
        results.AbortReason.ShouldContain("docker is not responding");
        results.AbortReason.ShouldContain("unknown state");
        results.ExitCode.ShouldBe(2);
        results.Tests.ShouldNotBeEmpty();
    }

    private sealed class FakeRecyclableResource(string name, Action? onRecycle = null) : IRecyclableResource
    {
        public string Name { get; } = name;
        public int RecycleCount { get; private set; }
        public Action? OnCheck { get; init; }

        public Task Check(CancellationToken token)
        {
            OnCheck?.Invoke();
            return Task.CompletedTask;
        }

        public Task Recycle(CancellationToken token = default)
        {
            RecycleCount++;
            onRecycle?.Invoke();
            return Task.CompletedTask;
        }

        public Task Start() => Task.CompletedTask;
        public Task ResetBetweenScenarios() => Task.CompletedTask;
        public ValueTask DisposeAsync() => default;
    }

    // ---------------------------------------------------------------- crash handling

    [Fact]
    public async Task a_test_a_dead_worker_never_reported_is_indeterminate_not_failed()
    {
        // The #43 spike measured 0-of-9 outcomes surviving a crash on some hosts. Treating
        // silence as failure would invent results the run never observed.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("ran"), FakeWorkerFactory.Test("never_reported")],
            Outcome = (uid, _, _) => uid == "ran" ? WorkerTestState.Passed : null,
            Fault = w => w.Runs.Count > 0 ? "the worker closed the connection" : null
        };

        var results = await build(factory).Run();

        var lost = results.Tests.Single(t => t.Uid == "never_reported");
        lost.IsIndeterminate.ShouldBeTrue();
        lost.Final.Outcome.State.ShouldBe(WorkerTestState.Indeterminate);

        // "We do not know what happened" is not an ordinary red build.
        results.Indeterminate.ShouldHaveSingleItem();
        results.ExitCode.ShouldBe(2);
    }

    [Fact]
    public async Task an_indeterminate_test_carries_the_reason_its_worker_died()
    {
        // "Indeterminate" with no explanation tells a user something went wrong and nothing
        // they can act on.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("lost")],
            Outcome = (_, _, _) => null,
            Fault = w => w.Runs.Count > 0 ? "the worker exited with code 70" : null
        };

        var results = await build(factory).Run();

        results.Tests.ShouldHaveSingleItem()
            .Final.Outcome.ErrorMessage.ShouldBe("the worker exited with code 70");

        results.WorkerFaults.ShouldContain("the worker exited with code 70");
        results.Summarize().ShouldContain("exited with code 70");
    }

    [Fact]
    public void a_test_nobody_reported_without_a_crash_says_exactly_that()
    {
        var completed = MtpWorkerClient.Complete(["asked"], [], fault: null);

        completed.ShouldHaveSingleItem()
            .ErrorMessage.ShouldBe("the worker finished without reporting a result for this test");
    }

    [Fact]
    public async Task a_dead_shared_worker_is_replaced_before_the_next_retry()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("flaky", "Retry=3")],
            Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Failed,
            // Only the first running worker dies.
            Fault = w => w.Index == 1 && w.Runs.Count > 0 ? "worker died" : null
        };

        var results = await build(factory, new RetryBudget { MaxAttemptsPerTest = 3 }).Run();

        factory.RunningWorkers.Count.ShouldBe(2);
        results.Tests.ShouldHaveSingleItem().Outcome.ShouldBe(RunOutcome.PassOnRetry);
    }

    // ---------------------------------------------------------------- preflight

    [Fact]
    public async Task a_failing_preflight_aborts_before_a_single_worker_is_launched()
    {
        // The Playwright-never-renders case: if the environment is broken, launching workers
        // just buys thousands of identical failures.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = build(factory);
        supervisor.Preflight.Add("docker is running", () => throw new InvalidOperationException("connection refused"));

        var results = await supervisor.Run();

        factory.Launched.ShouldBeEmpty(); // not even the discovery worker
        results.AbortReason.ShouldContain("connection refused");
        results.ExitCode.ShouldBe(2);
    }

    [Fact]
    public async Task recyclable_resources_are_checked_during_preflight()
    {
        // Supervisor-owned infrastructure is the only thing the supervisor can check itself.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = build(factory);
        supervisor.AddRecyclableResource(new FakeRecyclableResource("rabbit")
        {
            OnCheck = () => throw new InvalidOperationException("broker unreachable")
        });

        var results = await supervisor.Run();

        results.AbortReason.ShouldContain("rabbit");
        results.AbortReason.ShouldContain("broker unreachable");
        factory.Launched.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_passing_preflight_lets_the_run_proceed()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = build(factory);
        supervisor.Preflight.Add("docker is running", () => { });

        var results = await supervisor.Run();

        results.AbortReason.ShouldBeNull();
        results.CleanPasses.ShouldHaveSingleItem();
    }

    // ---------------------------------------------------------------- abort

    [Fact]
    public async Task a_catastrophic_policy_decision_aborts_the_run()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("doomed"), FakeWorkerFactory.Test("later", "Isolated=true")],
            Outcome = (_, _, _) => WorkerTestState.Error
        };

        var supervisor = build(factory);
        supervisor.AddFailurePolicy(new AbortOnAnything());

        var results = await supervisor.Run();

        results.AbortReason.ShouldNotBeNull();
        results.ExitCode.ShouldBe(2);

        // The isolated test never got its worker — aborting means stopping.
        factory.RunningWorkers.Count.ShouldBe(1);
    }

    private class AbortOnAnything : IFailurePolicy
    {
        public Disposition? Decide(AttemptContext attempt)
            => attempt.Succeeded ? null : Disposition.AbortRun("the environment is gone");
    }

    // ---------------------------------------------------------------- protocol guards

    [Fact]
    public void an_unfiltered_run_is_treated_as_a_protocol_fault()
    {
        // The single most dangerous MTP behaviour found in the spike: a wrong subset parameter
        // is ignored silently and the whole suite runs. A retry that did that would launder
        // unrelated failures into the attempt.
        var outcomes = new[]
        {
            new WorkerOutcome("asked_for", "asked_for", WorkerTestState.Passed),
            new WorkerOutcome("not_asked_for", "not_asked_for", WorkerTestState.Failed)
        };

        var exception = Should.Throw<WorkerProtocolException>(
            () => MtpWorkerClient.GuardAgainstAnUnfilteredRun(["asked_for"], outcomes));

        exception.Message.ShouldContain("not_asked_for");
        exception.Message.ShouldContain("not filtered");
    }

    [Fact]
    public void a_correctly_filtered_run_passes_the_guard()
    {
        Should.NotThrow(() => MtpWorkerClient.GuardAgainstAnUnfilteredRun(
            ["a", "b"],
            [new WorkerOutcome("a", "a", WorkerTestState.Passed)]));
    }
}
