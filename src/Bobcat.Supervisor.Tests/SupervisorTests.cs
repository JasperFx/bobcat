using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

public class SupervisorTests
{
    private static Supervisor Build(FakeWorkerFactory factory, RetryBudget? budget = null)
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

        await Build(factory).Run();

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

        await Build(factory).Run();

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

        var results = await Build(factory).Run();

        factory.RunningWorkers.Count.ShouldBe(2);
        // Isolation is not free, and the report says how much it cost.
        results.WorkersLaunched.ShouldBe(3); // 1 discovery + 2 isolated
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

        var results = await Build(factory, new RetryBudget { MaxAttemptsPerTest = 2 }).Run();

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

        var results = await Build(factory, new RetryBudget { MaxAttemptsPerTest = 2 }).Run();

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

        var results = await Build(factory, new RetryBudget { MaxAttemptsPerTest = 3 }).Run();

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

        var results = await Build(factory, new RetryBudget { MaxAttemptsPerTest = 5 }).Run();

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

        var results = await Build(factory, new RetryBudget { MaxAttemptsPerTest = 2 }).Run();

        results.CleanPasses.ShouldHaveSingleItem().Uid.ShouldBe("solid");
        results.PassedOnRetry.ShouldHaveSingleItem().Uid.ShouldBe("flaky");

        // Green build, but the flakiness is on the record rather than folded into the passes.
        results.ExitCode.ShouldBe(0);
        results.Summarize().ShouldContain("1 passed on retry");
    }

    [Fact]
    public async Task a_recycle_disposition_is_recorded_as_unhonoured_rather_than_downgraded()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("brokered", "RecycleOnRetry=rabbit", "Retry=2")],
            Outcome = (_, _, _) => WorkerTestState.Failed
        };

        var results = await Build(factory, new RetryBudget { MaxAttemptsPerTest = 2 }).Run();

        var test = results.Tests.ShouldHaveSingleItem();
        test.AttemptCount.ShouldBe(1); // not retried — quietly retrying without recycling would be a lie

        var unsupported = test.UnsupportedDispositions.ShouldHaveSingleItem();
        unsupported.ShouldContain("RetryAfterRecycle(rabbit)");
        unsupported.ShouldContain("NOT retried");
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

        var results = await Build(factory).Run();

        var lost = results.Tests.Single(t => t.Uid == "never_reported");
        lost.IsIndeterminate.ShouldBeTrue();
        lost.Final.Outcome.State.ShouldBe(WorkerTestState.Indeterminate);

        // "We do not know what happened" is not an ordinary red build.
        results.Indeterminate.ShouldHaveSingleItem();
        results.ExitCode.ShouldBe(2);
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

        var results = await Build(factory, new RetryBudget { MaxAttemptsPerTest = 3 }).Run();

        factory.RunningWorkers.Count.ShouldBe(2);
        results.Tests.ShouldHaveSingleItem().Outcome.ShouldBe(RunOutcome.PassOnRetry);
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

        var supervisor = Build(factory);
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
