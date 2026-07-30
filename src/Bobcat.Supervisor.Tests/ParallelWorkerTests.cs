using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// The parallel worker pool: does the fleet actually form, does every test still run exactly once,
/// and does a class stay in one process.
/// </summary>
public class ParallelWorkerTests
{
    private static IReadOnlyList<WorkerTest> Classes(params (string Name, int Count)[] classes)
        => classes
            .SelectMany(c => Enumerable.Range(1, c.Count).Select(i => FakeWorkerFactory.InClass(c.Name, $"test_{i}")))
            .ToList();

    private static Supervisor Supervised(FakeWorkerFactory factory, int workers, RetryBudget? budget = null)
        => new(factory)
        {
            MaxParallelWorkers = workers,
            RetryBudget = budget ?? RetryBudget.None
        };

    [Fact]
    public async Task one_worker_is_still_the_default_and_still_runs_one_batch()
    {
        // The upgrade guarantee: an existing caller that never heard of MaxParallelWorkers gets
        // exactly the run it got before.
        var factory = new FakeWorkerFactory
        {
            Tests = Classes(("A", 4), ("B", 4)),
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var results = await new Supervisor(factory).Run();

        results.Tests.Count.ShouldBe(8);
        factory.RunningWorkers.ShouldHaveSingleItem();
        results.WorkersLaunched.ShouldBe(2); // one throwaway for discovery, one for the batch
    }

    [Fact]
    public async Task four_lanes_launch_four_workers_and_run_every_test_exactly_once()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = Classes(("A", 4), ("B", 4), ("C", 4), ("D", 4)),
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var results = await Supervised(factory, workers: 4).Run();

        factory.RunningWorkers.Count.ShouldBe(4);
        results.Tests.Count.ShouldBe(16);
        results.CleanPasses.Count.ShouldBe(16);

        // Exactly once: no test asked for twice, none dropped.
        var asked = factory.RunningWorkers.SelectMany(w => w.Runs).SelectMany(r => r!).ToList();
        asked.Count.ShouldBe(16);
        asked.Distinct().Count().ShouldBe(16);
    }

    [Fact]
    public async Task a_class_never_spans_two_workers()
    {
        // The correctness property the whole feature rests on. A class keeping static state
        // (a counter naming a schema, a cached connection) assumes one process.
        var factory = new FakeWorkerFactory
        {
            Tests = Classes(("A", 5), ("B", 5), ("C", 5)),
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await Supervised(factory, workers: 3).Run();

        foreach (var className in new[] { "A", "B", "C" })
        {
            var workersTouchingClass = factory.RunningWorkers
                .Count(w => w.Runs.SelectMany(r => r!).Any(uid => uid.StartsWith(className + ".")));

            workersTouchingClass.ShouldBe(1);
        }
    }

    [Fact]
    public async Task more_workers_than_classes_does_not_launch_idle_processes()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = Classes(("A", 3), ("B", 3)),
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await Supervised(factory, workers: 16).Run();

        factory.RunningWorkers.Count.ShouldBe(2);
    }

    [Fact]
    public async Task isolated_tests_still_get_their_own_process_alongside_the_lanes()
    {
        var tests = Classes(("A", 3), ("B", 3)).Append(FakeWorkerFactory.Test("Lonely.test", "Isolated=true")).ToList();

        var factory = new FakeWorkerFactory
        {
            Tests = tests,
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await Supervised(factory, workers: 2).Run();

        // Two lanes plus a dedicated process for the isolated test.
        factory.RunningWorkers.Count.ShouldBe(3);
        factory.RunningWorkers
            .Count(w => w.Runs.SelectMany(r => r!).SequenceEqual(new[] { "Lonely.test" }))
            .ShouldBe(1);
    }

    [Fact]
    public async Task a_same_process_retry_goes_back_to_the_lane_the_test_ran_in()
    {
        // "Same process" has to mean the same one. A class's static state lives in the process
        // that ran it, so retrying in a different warm worker is a fresh-process retry wearing
        // the wrong label.
        // Retries stay opt-in even in a fleet, so the flaky test carries the tag.
        var tests = Classes(("A", 3), ("B", 3))
            .Append(FakeWorkerFactory.InClass("C", "test_1"))
            .Append(FakeWorkerFactory.InClass("C", "test_2", "Retry=2"))
            .Append(FakeWorkerFactory.InClass("C", "test_3"))
            .ToList();

        var factory = new FakeWorkerFactory
        {
            Tests = tests,
            Outcome = (uid, attempt, _) =>
                uid == "C.test_2" && attempt == 1 ? WorkerTestState.Failed : WorkerTestState.Passed
        };

        var results = await Supervised(factory, workers: 3,
            budget: new RetryBudget { MaxAttemptsPerTest = 2 }).Run();

        results.Tests.Single(t => t.Uid == "C.test_2").Outcome.ShouldBe(RunOutcome.PassOnRetry);

        // The retry landed in the worker that already held class C, and no new one was launched.
        var retryWorker = factory.RunningWorkers
            .Single(w => w.Runs.Count > 1 && w.Runs[^1]!.Contains("C.test_2"));

        retryWorker.Runs[0]!.ShouldContain("C.test_1");
        factory.RunningWorkers.Count.ShouldBe(3);
    }

    [Fact]
    public async Task a_lane_whose_worker_dies_does_not_take_the_other_lanes_with_it()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = Classes(("A", 3), ("B", 3), ("C", 3)),
            // Worker index 1 is the first lane (0 is the discovery throwaway).
            Fault = worker => worker.Index == 1 ? "worker died (exit code 70)" : null,
            Outcome = (uid, _, worker) => worker.Index == 1 && uid.EndsWith("_3")
                ? null                      // withheld: the process died before reporting
                : WorkerTestState.Passed
        };

        var results = await Supervised(factory, workers: 3).Run();

        // The dead lane's unreported test is Indeterminate, never invented as a failure...
        results.Indeterminate.ShouldHaveSingleItem();
        results.WorkerFaults.ShouldHaveSingleItem().ShouldContain("exit code 70");

        // ...and the other two lanes' results survived intact.
        results.CleanPasses.Count.ShouldBe(8);
        results.ExitCode.ShouldBe(2); // indeterminate is not an ordinary red build
    }

    [Fact]
    public async Task results_are_reported_in_a_stable_order_regardless_of_which_lane_finishes_first()
    {
        // Lanes complete in whatever order the OS decides; the report must not.
        var factory = new FakeWorkerFactory
        {
            Tests = Classes(("A", 3), ("B", 3), ("C", 3), ("D", 3)),
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var first = await Supervised(factory, workers: 4).Run();

        var second = await Supervised(new FakeWorkerFactory
        {
            Tests = Classes(("A", 3), ("B", 3), ("C", 3), ("D", 3)),
            Outcome = (_, _, _) => WorkerTestState.Passed
        }, workers: 4).Run();

        first.Tests.Select(t => t.Uid).ShouldBe(second.Tests.Select(t => t.Uid));
    }

    [Fact]
    public async Task known_durations_drive_the_split_across_lanes()
    {
        // End to end: the balancing input actually reaches the plan.
        var tests = Classes(("Slow", 1), ("Fast", 12));

        var factory = new FakeWorkerFactory
        {
            Tests = tests,
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = Supervised(factory, workers: 2);
        supervisor.KnownTestDurations = new Dictionary<string, TimeSpan>
        {
            ["Slow.test_1"] = TimeSpan.FromSeconds(90)
        };

        await supervisor.Run();

        // The 90s test is alone in its lane despite being 1 test against 12.
        factory.RunningWorkers
            .Count(w => w.Runs.SelectMany(r => r!).SequenceEqual(new[] { "Slow.test_1" }))
            .ShouldBe(1);
    }

    [Fact]
    public async Task every_worker_is_disposed_when_the_run_ends()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = Classes(("A", 3), ("B", 3), ("C", 3)),
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await Supervised(factory, workers: 3).Run();

        factory.Launched.ShouldAllBe(w => w.Disposed);
    }
}
