using Bobcat.Ledger;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// The supervisor's feed into the committed ledger (docs/ledger-design.md), and the loop that
/// makes the ledger infrastructure rather than a report: durations recorded from one run feed
/// <see cref="Supervisor.KnownTestDurations"/> on the next, so WorkPlan balances lanes by
/// measured cost from the first pass instead of only after a warm-up.
/// </summary>
public class LedgerFeedTests
{
    private static readonly DateTimeOffset t0 = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task a_supervised_run_records_failure_classes_and_what_cleared_them()
    {
        var factory = new FakeWorkerFactory
        {
            Tests =
            [
                FakeWorkerFactory.Test("Suite/steady"),
                FakeWorkerFactory.Test("Suite/flaky", "Retry=2")
            ],
            // The flaky test fails once with a typed exception, then recovers on the retry.
            Outcome = (uid, attempt, _) =>
                uid == "Suite/flaky" && attempt == 1 ? WorkerTestState.Error : WorkerTestState.Passed,
            ErrorType = (_, _) => "System.TimeoutException",
            Duration = (uid, _) => uid == "Suite/steady" ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(1)
        };

        var supervisor = new Supervisor(factory)
        {
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 }
        };

        var results = await supervisor.Run();
        var observations = SupervisorLedger.From(results, "run-1", t0);

        var flaky = observations.Single(o => o.Uid == "Suite/flaky");
        flaky.Outcome.ShouldBe(nameof(RunOutcome.PassOnRetry));
        flaky.Attempts.ShouldBe(2);
        // The failure class and its recovery — what a hint proposal is made from, and what the
        // in-process feed cannot know (only the final attempt survives there).
        flaky.Failure.ShouldBe("System.TimeoutException");
        flaky.ClearedBy.ShouldBe(nameof(JasperFx.Testing.DispositionKind.RetryInFreshProcess));
        flaky.TotalMs.ShouldBe(2_000);
        flaky.FirstMs.ShouldBe(1_000);
        flaky.StallInduced.ShouldBeFalse();

        var steady = observations.Single(o => o.Uid == "Suite/steady");
        steady.Outcome.ShouldBe(nameof(RunOutcome.CleanPass));
        steady.Failure.ShouldBeNull();

        // The loop closes: this run's ledger is the next run's balancer feed.
        var ledger = TestLedger.Empty().Record(observations);
        var next = new Supervisor(factory) { KnownTestDurations = ledger.KnownDurations() };
        next.KnownTestDurations!["Suite/steady"].ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task unmeasured_durations_reach_the_ledger_as_absent_not_zero()
    {
        // tUnit erases durations on the MTP wire; the fake's default models exactly that.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Suite/unmeasured")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var results = await new Supervisor(factory).Run();
        var observed = SupervisorLedger.From(results, "run-1", t0).ShouldHaveSingleItem();

        observed.TotalMs.ShouldBeNull();
        observed.FirstMs.ShouldBeNull();
        TestLedger.Empty().Record([observed]).KnownDurations().ShouldBeEmpty();
    }

    private static FakeWorkerFactory machine(string uidPrefix) => new()
    {
        // The same four tests, under the uids another machine's assembly path hashes to.
        Tests =
        [
            new WorkerTest($"{uidPrefix}-1", "A.one"), new WorkerTest($"{uidPrefix}-2", "A.two"),
            new WorkerTest($"{uidPrefix}-3", "B.one"), new WorkerTest($"{uidPrefix}-4", "B.two")
        ],
        Outcome = (_, _, _) => WorkerTestState.Passed,
        Duration = (uid, _) => uid.EndsWith("-1") ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(1)
    };

    [Fact]
    public async Task a_ledger_recorded_on_one_machine_matches_every_test_on_another()
    {
        // Issue #730: keyed by uid, this matched 0 of 4 — Marten's 0 of 2525 in miniature.
        var laptop = await new Supervisor(machine("laptop")).Run();
        var ledger = TestLedger.Empty().Record(SupervisorLedger.From(laptop, "run-1", t0));

        ledger.Tests.Keys.ShouldBe(["A.one", "A.two", "B.one", "B.two"]);

        var ci = await new Supervisor(machine("ci"))
        {
            MaxParallelWorkers = 2,
            KnownTestDurations = TestLedger.FromJson(ledger.ToJson()).KnownDurations()
        }.Run();

        ci.DurationCoverage.ShouldBe(new DurationCoverage(4, 4));
    }

    [Fact]
    public async Task two_tests_sharing_a_display_name_are_recorded_and_looked_up_by_uid()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [new WorkerTest("u1", "Ns.Class.same"), new WorkerTest("u2", "Ns.Class.same")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            Duration = (uid, _) => uid == "u1" ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(9)
        };

        var results = await new Supervisor(factory).Run();
        var observations = SupervisorLedger.From(results, "run-1", t0);

        // Never one test with two tests' durations folded into it.
        observations.Select(o => o.Key).OrderBy(k => k).ShouldBe(["u1", "u2"]);
        var ledger = TestLedger.Empty().Record(observations);
        ledger.SharedDisplayNames.ShouldBe(["Ns.Class.same"]);

        var next = await new Supervisor(factory)
        {
            MaxParallelWorkers = 2,
            KnownTestDurations = ledger.KnownDurations()
        }.Run();
        next.DurationCoverage.ShouldBe(new DurationCoverage(2, 2));
    }
}
