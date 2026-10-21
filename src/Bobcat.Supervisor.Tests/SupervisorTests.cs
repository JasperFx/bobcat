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

    [Fact]
    public async Task a_filter_keeping_nothing_aborts_the_run_when_no_tests_is_a_failure()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a"), FakeWorkerFactory.Test("b")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = build(factory);
        supervisor.TestFilter = _ => false;
        supervisor.FailWhenNoTests = true;

        var results = await supervisor.Run();

        results.Tests.ShouldBeEmpty();
        results.AbortReason.ShouldNotBeNull();
        results.AbortReason.ShouldContain("2 test(s) were discovered and the selection kept none");
        results.ExitCode.ShouldBe(2);
        factory.RunningWorkers.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_worker_discovering_nothing_aborts_the_run_when_no_tests_is_a_failure()
    {
        var factory = new FakeWorkerFactory { Tests = [], Outcome = (_, _, _) => WorkerTestState.Passed };

        var supervisor = build(factory);
        supervisor.FailWhenNoTests = true;

        var results = await supervisor.Run();

        results.AbortReason!.ShouldContain("the worker discovered none");
        results.ExitCode.ShouldBe(2);
    }

    [Fact]
    public async Task no_tests_is_not_a_failure_by_itself_when_something_ran()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = build(factory);
        supervisor.FailWhenNoTests = true;

        var results = await supervisor.Run();

        results.AbortReason.ShouldBeNull();
        results.ExitCode.ShouldBe(0);
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
            Tests = [FakeWorkerFactory.Test("flaky", "Retry=2", "RetryWarm=true")],
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
    public async Task a_plain_retry_trait_retries_alone_in_a_fresh_process()
    {
        // bobcat#661: the warm worker may hold whatever the failed attempt left behind, so the
        // default retry gets a process of its own, the only reset bracket a worker has.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("flaky", "Retry=2"), FakeWorkerFactory.Test("clean")],
            Outcome = (uid, attempt, _) =>
                uid == "clean" || attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Failed
        };

        var results = await build(factory, new RetryBudget { MaxAttemptsPerTest = 2 }).Run();

        var flaky = results.Tests.Single(t => t.Uid == "flaky");
        flaky.Outcome.ShouldBe(RunOutcome.PassOnRetry);
        flaky.Attempts[0].Disposition.Kind.ShouldBe(DispositionKind.RetryInFreshProcess);
        flaky.Attempts[1].Placement.ShouldBe(AttemptPlacement.IsolatedProcess);

        factory.RunningWorkers.Count.ShouldBe(2);
        factory.RunningWorkers[1].Runs.ShouldHaveSingleItem().ShouldBe(["flaky"]);
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

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetBetweenScenarios() => Task.CompletedTask;
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
    public async Task an_indeterminate_test_is_named_by_what_discovery_called_it()
    {
        // The client only knows the uid it asked for, and on some front-ends a uid is a hash —
        // an environment-dependent xUnit theory identity was the real case. The supervisor ran
        // discovery, so it still has the name a human can act on.
        var hash = "386e2dca486bade0f9bd72d9bc19eeb05d67185ef983c37405f5d0bda2c3e703";
        var factory = new FakeWorkerFactory
        {
            Tests = [new WorkerTest(hash, "MartenTests.can_persist(mode: Lightweight)")],
            Outcome = (_, _, _) => null,
            Fault = w => w.Runs.Count > 0 ? "the worker exited with code 134" : null
        };

        var lost = (await build(factory).Run()).Indeterminate.ShouldHaveSingleItem();

        lost.DisplayName.ShouldBe("MartenTests.can_persist(mode: Lightweight)");
        lost.Final.Outcome.DisplayName.ShouldBe("MartenTests.can_persist(mode: Lightweight)");

        // The uid is still the identity — only the name it is reported under changed.
        lost.Uid.ShouldBe(hash);
    }

    [Fact]
    public async Task a_name_the_worker_reported_is_never_overwritten_by_discovery()
    {
        // Naming is a fallback for a missing name. A worker that reports a name — a theory case
        // it resolved at run time, say — knows more about that attempt than discovery did.
        var factory = new FakeWorkerFactory
        {
            Tests = [new WorkerTest("uid-1", "what discovery called it")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            ReportedName = _ => "what the run called it"
        };

        var results = await build(factory).Run();

        results.Tests.ShouldHaveSingleItem()
            .DisplayName.ShouldBe("what the run called it");
    }

    [Fact]
    public void a_test_nobody_reported_without_a_crash_says_exactly_that()
    {
        var completed = MtpWorkerClient.Complete(["asked"], [], fault: null);

        completed.ShouldHaveSingleItem()
            .ErrorMessage.ShouldBe("the worker finished without reporting a result for this test");
    }

    [Fact]
    public void the_test_that_was_running_when_the_worker_died_is_explained_too()
    {
        // Nodes arrive in-progress then final, and the in-progress update is recorded — so the
        // test executing when the process died has an entry already and the "missing" pass
        // skips it. It used to reach a report as an indeterminate with no explanation: the one
        // test a reader most wants explained.
        var running = new WorkerOutcome("running", "Arithmetic: slow", WorkerTestState.Indeterminate);

        var completed = MtpWorkerClient.Complete(
            ["running", "never reached"], [running], "the worker exited with code 70");

        completed.ShouldAllBe(o => o.ErrorMessage!.Contains("code 70"));
    }

    [Fact]
    public void a_verdict_the_worker_did_give_is_never_overwritten_by_the_fault()
    {
        var failed = new WorkerOutcome("decided", "Arithmetic: adds", WorkerTestState.Failed)
        {
            ErrorMessage = "expected 4, got 5"
        };

        MtpWorkerClient.Complete(["decided"], [failed], "the worker exited with code 70")
            .ShouldHaveSingleItem()
            .ErrorMessage.ShouldBe("expected 4, got 5");
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

    [Theory]
    [InlineData("Bobcat.Engine.SpecCatastrophicException", WorkerTestState.Failed)]
    [InlineData("SpecCatastrophicException", WorkerTestState.Error)]
    public async Task a_worker_reporting_a_catastrophic_exception_aborts_the_run_under_the_default_policy(
        string errorType, WorkerTestState state)
    {
        // Issue #538: no custom policy. The type is only a name off the wire, and a Retry trait
        // with budget to spare must not turn the environment being gone into a retry.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("doomed", "Retry=3"), FakeWorkerFactory.Test("later", "Isolated=true")],
            Outcome = (uid, _, _) => uid == "doomed" ? state : WorkerTestState.Passed,
            ErrorType = (uid, _) => uid == "doomed" ? errorType : null
        };

        var results = await build(factory, new RetryBudget { MaxAttemptsPerTest = 3 }).Run();

        results.AbortReason.ShouldNotBeNull();
        results.ExitCode.ShouldBe(2);
        results.Tests.Single(t => t.Uid == "doomed").AttemptCount.ShouldBe(1);
        factory.RunningWorkers.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(null)] // TUnit erases the type on the wire: degrade to today's behaviour
    [InlineData("MyApp.SpecCatastrophicException")] // a lookalike in someone else's namespace
    public async Task a_failure_not_named_as_bobcats_catastrophic_exception_does_not_abort(string? errorType)
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("doomed", "Retry=3")],
            Outcome = (_, attempt, _) => attempt == 1 ? WorkerTestState.Error : WorkerTestState.Passed,
            ErrorType = (_, _) => errorType
        };

        var results = await build(factory, new RetryBudget { MaxAttemptsPerTest = 3 }).Run();

        // Critical stays retryable under a Retry trait — the decision recorded on #538.
        results.AbortReason.ShouldBeNull();
        results.Tests.Single().Outcome.ShouldBe(RunOutcome.PassOnRetry);
    }

    [Theory]
    [InlineData("Bobcat.Engine.SpecCatastrophicException", true)]
    [InlineData("SpecCatastrophicException", true)]
    // What a worker's later scenarios fail with once its environment is lost (bobcat#539).
    [InlineData("Bobcat.Engine.SpecEnvironmentLostException", true)]
    [InlineData("SpecEnvironmentLostException", true)]
    [InlineData("Bobcat.Engine.SpecCriticalException", false)]
    [InlineData("MyApp.SpecCatastrophicException", false)]
    [InlineData(null, false)]
    public void a_reported_type_is_catastrophic_by_its_simple_or_full_name(string? errorType, bool expected)
        => Supervisor.IsCatastrophic(errorType).ShouldBe(expected);

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

    /// <summary>The ad hoc check docs/parallel-ready-suites.md shows: a delegate that throws to fail.</summary>
    [Fact]
    public async Task a_preflight_check_is_a_delegate_that_throws_to_fail()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        // #region preflight-check
        var supervisor = new Supervisor(factory);

        // A check that throws has failed. This one asks whether anything answers on the database's port.
        supervisor.Preflight.Add("Postgres is reachable", async token =>
        {
            using var probe = new System.Net.Sockets.TcpClient();
            await probe.ConnectAsync("127.0.0.1", port, token);
        });
        // #endregion preflight-check

        var results = await supervisor.Run();

        results.AbortReason.ShouldBeNull();
        results.CleanPasses.ShouldHaveSingleItem();
    }
}
