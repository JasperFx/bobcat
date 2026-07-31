using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// The sweep's failure mode is reporting zero — a sweep that swept nothing, or whose isolation was
/// not isolating, is indistinguishable from a clean suite. So these tests lead with a *planted*
/// order-dependent test rather than with the happy path, and the clean-suite test asserts what was
/// swept rather than only that nothing was found.
/// </summary>
public class IsolationSweepTests
{
    private static readonly IReadOnlyList<WorkerTest> TwoClasses =
    [
        FakeWorkerFactory.InClass("Ns.Alpha", "one"),
        FakeWorkerFactory.InClass("Ns.Alpha", "two"),
        FakeWorkerFactory.InClass("Ns.Beta", "three"),
        FakeWorkerFactory.InClass("Ns.Beta", "four")
    ];

    /// <summary>Ran with company, so <c>uids</c> is the whole suite rather than a single test.</summary>
    private static bool ranWithCompany(FakeWorker worker) =>
        worker.Runs.Count == 0 || worker.Runs[^1] is null || worker.Runs[^1]!.Count > 1;

    // ---------------------------------------------------------------- the control

    [Fact]
    public async Task finds_a_test_that_only_passes_when_something_else_ran_first()
    {
        // The planted defect: green in company, red alone. This is the whole point of the feature,
        // and a sweep that cannot report it is a broken instrument however clean its output looks.
        var factory = new FakeWorkerFactory
        {
            Tests = TwoClasses,
            Outcome = (uid, _, worker) => uid.EndsWith("three") && !ranWithCompany(worker)
                ? WorkerTestState.Failed
                : WorkerTestState.Passed
        };

        var results = await new IsolationSweep(factory)
        {
            Granularity = SweepGranularity.PerTest
        }.Run();

        results.OrderDependent.Select(f => f.Uid).ShouldBe(["Ns.Beta.three"]);
        results.IsClean.ShouldBeFalse();
    }

    [Fact]
    public async Task a_clean_suite_reports_nothing_but_still_says_what_it_swept()
    {
        // Guards the false zero: "no findings" is only meaningful alongside evidence that the
        // sweep actually ran something.
        var factory = new FakeWorkerFactory
        {
            Tests = TwoClasses,
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var results = await new IsolationSweep(factory) { Granularity = SweepGranularity.PerTest }.Run();

        results.Findings.ShouldBeEmpty();
        results.IsClean.ShouldBeTrue();
        results.Discovered.ShouldBe(4);
        results.Partitions.ShouldBe(4);
    }

    // ---------------------------------------------------------------- the other verdicts

    [Fact]
    public async Task a_test_that_fails_only_in_company_is_an_interference_victim()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = TwoClasses,
            Outcome = (uid, _, worker) => uid.EndsWith("one") && ranWithCompany(worker)
                ? WorkerTestState.Failed
                : WorkerTestState.Passed
        };

        var results = await new IsolationSweep(factory) { Granularity = SweepGranularity.PerTest }.Run();

        results.InterferenceVictims.Select(f => f.Uid).ShouldBe(["Ns.Alpha.one"]);
        results.OrderDependent.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_test_that_fails_both_ways_is_ordinary_red_not_an_isolation_finding()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = TwoClasses,
            Outcome = (uid, _, _) => uid.EndsWith("four") ? WorkerTestState.Failed : WorkerTestState.Passed
        };

        var results = await new IsolationSweep(factory) { Granularity = SweepGranularity.PerTest }.Run();

        results.FailedInBoth.Select(f => f.Uid).ShouldBe(["Ns.Beta.four"]);
        results.OrderDependent.ShouldBeEmpty();

        // It failed, but not because of ordering — so it must not make the suite look unsafe to split.
        results.IsClean.ShouldBeTrue();
    }

    [Fact]
    public async Task a_failure_that_clears_on_the_confirmation_run_is_a_confound_not_a_finding()
    {
        // Attempt 1 is the baseline, 2 is the concurrent sweep, 3 is the serial confirmation on
        // lane 0. Failing only on 2 models a suite that broke under the sweep's own per-worker
        // database rather than under isolation — exactly the confound the confirmation pass exists
        // to separate out.
        var factory = new FakeWorkerFactory
        {
            Tests = TwoClasses,
            Outcome = (uid, attempt, _) => uid.EndsWith("two") && attempt == 2
                ? WorkerTestState.Failed
                : WorkerTestState.Passed
        };

        var results = await new IsolationSweep(factory)
        {
            Granularity = SweepGranularity.PerTest,
            MaxParallelWorkers = 2
        }.Run();

        results.EnvironmentSensitive.Select(f => f.Uid).ShouldBe(["Ns.Alpha.two"]);
        results.OrderDependent.ShouldBeEmpty();
        results.IsClean.ShouldBeTrue();
    }

    [Fact]
    public async Task the_confirmation_run_is_what_promotes_a_suspect_to_order_dependent()
    {
        // Same shape as the confound above but failing on both isolated attempts. Together these
        // two prove the verdict is decided by the confirmation, not by the sweep alone.
        var factory = new FakeWorkerFactory
        {
            Tests = TwoClasses,
            Outcome = (uid, attempt, _) => uid.EndsWith("two") && attempt >= 2
                ? WorkerTestState.Failed
                : WorkerTestState.Passed
        };

        var results = await new IsolationSweep(factory)
        {
            Granularity = SweepGranularity.PerTest,
            MaxParallelWorkers = 2
        }.Run();

        results.OrderDependent.Select(f => f.Uid).ShouldBe(["Ns.Alpha.two"]);
        results.EnvironmentSensitive.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- granularity

    [Fact]
    public async Task per_class_granularity_runs_one_process_per_class()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = TwoClasses,
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var results = await new IsolationSweep(factory) { Granularity = SweepGranularity.PerClass }.Run();

        results.Partitions.ShouldBe(2);

        // Each isolation worker got a whole class, not a single test.
        var isolated = factory.Launched
            .Where(w => w.Launch.Purpose == WorkerPurpose.Isolated && w.Runs.Count > 0)
            .ToList();

        isolated.Count.ShouldBe(2);
        isolated.ShouldAllBe(w => w.Runs[0]!.Count == 2);
    }

    [Fact]
    public async Task per_class_granularity_cannot_see_ordering_inside_a_class()
    {
        // Honest about the limit rather than implying per-class finds everything: a test that
        // depends on its own classmate still passes when the class runs together.
        var factory = new FakeWorkerFactory
        {
            Tests = TwoClasses,
            Outcome = (uid, _, worker) => uid.EndsWith("three") && !ranWithCompany(worker)
                ? WorkerTestState.Failed
                : WorkerTestState.Passed
        };

        var perClass = await new IsolationSweep(factory) { Granularity = SweepGranularity.PerClass }.Run();
        perClass.OrderDependent.ShouldBeEmpty();

        // The same defect, found when the granularity is fine enough to expose it.
        var perTest = await new IsolationSweep(new FakeWorkerFactory
        {
            Tests = TwoClasses,
            Outcome = (uid, _, worker) => uid.EndsWith("three") && !ranWithCompany(worker)
                ? WorkerTestState.Failed
                : WorkerTestState.Passed
        })
        { Granularity = SweepGranularity.PerTest }.Run();

        perTest.OrderDependent.Select(f => f.Uid).ShouldBe(["Ns.Beta.three"]);
    }

    [Fact]
    public async Task a_custom_partition_key_groups_by_something_other_than_the_class()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = TwoClasses,
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var results = await new IsolationSweep(factory)
        {
            Granularity = SweepGranularity.PerClass,
            PartitionKey = _ => "everything together"
        }.Run();

        results.Partitions.ShouldBe(1);
    }

    // ---------------------------------------------------------------- scheduling and safety

    [Fact]
    public async Task no_more_lanes_are_used_than_were_asked_for()
    {
        // A caller provisions one database per lane, so a sweep that quietly used more would point
        // a worker at a database nobody created.
        var factory = new FakeWorkerFactory
        {
            Tests = TwoClasses,
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await new IsolationSweep(factory)
        {
            Granularity = SweepGranularity.PerTest,
            MaxParallelWorkers = 2
        }.Run();

        factory.Launched
            .Where(w => w.Launch.Purpose == WorkerPurpose.Isolated)
            .Select(w => w.Launch.Lane)
            .Distinct()
            .ShouldAllBe(lane => lane < 2);
    }

    [Fact]
    public async Task discovery_gets_a_throwaway_worker_of_its_own()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = TwoClasses,
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await new IsolationSweep(factory).Run();

        factory.Launched[0].Launch.Purpose.ShouldBe(WorkerPurpose.Discovery);
        factory.Launched[0].Runs.ShouldBeEmpty();
        factory.Launched[0].Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task a_crashed_baseline_aborts_rather_than_inventing_findings()
    {
        // Without a baseline there is nothing to compare against, and every "failed alone" would
        // be unclassifiable. Reporting nothing is honest; reporting half the evidence is not.
        var factory = new FakeWorkerFactory
        {
            Tests = TwoClasses,
            Outcome = (_, _, _) => WorkerTestState.Passed,
            Fault = worker => worker.Launch.Purpose == WorkerPurpose.Lane ? "worker died" : null
        };

        var results = await new IsolationSweep(factory).Run();

        results.AbortReason.ShouldNotBeNull();
        results.AbortReason.ShouldContain("worker died");
        results.Findings.ShouldBeEmpty();
        results.Partitions.ShouldBe(0);
    }

    [Fact]
    public async Task an_empty_suite_is_reported_as_empty_rather_than_as_clean()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var results = await new IsolationSweep(factory).Run();

        results.Discovered.ShouldBe(0);
        results.Partitions.ShouldBe(0);
        results.Findings.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_test_the_sweep_never_reported_on_is_not_read_as_a_pass()
    {
        // A worker that dies mid-run reports nothing for the rest. Silence must not become green.
        //
        // The guard for this actually lives upstream in MtpWorkerClient.Complete, which
        // synthesises a non-passing outcome for any requested uid the worker never spoke about.
        // The sweep inherits it, so a withheld test surfaces as a finding rather than vanishing.
        // IsolationSweep keeps its own belt-and-braces branch for an outcome that is missing
        // entirely, which is what a whole crashed group looks like.
        var factory = new FakeWorkerFactory
        {
            Tests = TwoClasses,
            Outcome = (uid, attempt, _) => uid.EndsWith("four") && attempt >= 2
                ? null                       // withheld: nothing reported for this test
                : WorkerTestState.Passed
        };

        var results = await new IsolationSweep(factory) { Granularity = SweepGranularity.PerTest }.Run();

        var finding = results.Findings.ShouldHaveSingleItem();
        finding.Uid.ShouldBe("Ns.Beta.four");
        finding.Verdict.ShouldBe(SweepVerdict.OrderDependent);
        finding.ErrorMessage.ShouldContain("without reporting a result");
    }

    [Fact]
    public async Task findings_carry_the_partition_they_were_swept_in()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = TwoClasses,
            Outcome = (uid, _, worker) => uid.EndsWith("three") && !ranWithCompany(worker)
                ? WorkerTestState.Failed
                : WorkerTestState.Passed
        };

        var results = await new IsolationSweep(factory) { Granularity = SweepGranularity.PerTest }.Run();

        results.OrderDependent.ShouldHaveSingleItem().Partition.ShouldBe("Ns.Beta.three");
    }
}
