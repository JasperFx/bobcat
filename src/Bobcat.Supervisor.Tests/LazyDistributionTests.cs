using System.Text.Json;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#577: <c>Supervisor.Distribution = Lazy</c> — lanes pull class partitions from a
/// longest-first queue, one request each, and the run is recorded in queue order whatever order the
/// lanes finish in. Concurrency is staged with held tests, as in <see cref="StallActionTests"/>: a
/// fake worker runs synchronously unless something holds it.
/// </summary>
public class LazyDistributionTests
{
    private static readonly TimeSpan waitBudget = TimeSpan.FromSeconds(20);

    private static IReadOnlyList<WorkerTest> classes(params (string Name, int Count)[] classes)
        => classes
            .SelectMany(c => Enumerable.Range(1, c.Count).Select(i => FakeWorkerFactory.InClass(c.Name, $"test_{i}")))
            .ToList();

    private static string classOf(IReadOnlyList<string>? request) => WorkPlan.ClassOf(new WorkerTest(request![0], request[0]));

    private sealed class RecordingObserver : ISupervisorObserver
    {
        public List<string> Recorded { get; } = [];
        public List<string> Finished { get; } = [];

        public void AttemptRecorded(string uid, SupervisorAttempt attempt)
        {
            lock (Recorded) Recorded.Add(uid);
        }

        public void TestUpdated(WorkerLaunchContext worker, WorkerTestUpdate update)
        {
            if (update.State is null) return;
            lock (Finished) Finished.Add(update.Uid);
        }
    }

    [Fact]
    public void the_queue_is_longest_first_then_by_key_and_a_random_queue_is_the_seeds_order()
    {
        var tests = classes(("A", 1), ("B", 3), ("C", 2), ("D", 2));
        var known = tests.ToDictionary(t => t.DisplayName, t => t.Uid.StartsWith("A.") ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(1));

        WorkPlan.Queue(tests, knownDurations: known).Select(p => p.Key).ShouldBe(["A", "B", "C", "D"]);

        // No durations: weighed by count, and C and D tie, so the key settles it.
        WorkPlan.Queue(tests).Select(p => p.Key).ShouldBe(["B", "C", "D", "A"]);

        // Random: exactly the order a one-lane random plan deals them in.
        var random = WorkPlan.Queue(tests, order: RunOrder.Random, seed: 42).Select(p => p.Key).ToList();
        random.ShouldBe(WorkPlan.Build(tests, 1, order: RunOrder.Random, seed: 42).Single().Partitions);

        // Classes are never split, and the tests inside one keep their order.
        WorkPlan.Queue(tests).Single(p => p.Key == "B").Uids.ShouldBe(["B.test_1", "B.test_2", "B.test_3"]);
    }

    [Fact]
    public async Task one_lane_runs_one_request_per_class_longest_first()
    {
        var factory = new FakeWorkerFactory { Tests = classes(("A", 1), ("B", 3), ("C", 2)), Outcome = (_, _, _) => WorkerTestState.Passed };

        var results = await new Supervisor(factory) { Distribution = Distribution.Lazy }.Run();

        results.CleanPasses.Count.ShouldBe(6);
        results.Distribution.ShouldBe(Distribution.Lazy);

        var worker = factory.RunningWorkers.ShouldHaveSingleItem();
        worker.Runs.Select(classOf).ShouldBe(["B", "C", "A"]);
    }

    [Fact]
    public async Task idle_lanes_pull_the_rest_while_one_lane_is_held_and_the_run_is_recorded_in_queue_order()
    {
        // A is the longest class, so lane 0 pulls it first and is held there until the last of the
        // others has started; lane 1 drains everything else meanwhile. Eager would have dealt the
        // lanes fixed shares up front.
        var lastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new RecordingObserver();

        var factory = new FakeWorkerFactory
        {
            Tests = classes(("A", 3), ("B", 1), ("C", 1), ("D", 1)),
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = async (uid, _) =>
            {
                if (uid == "D.test_1") lastStarted.TrySetResult();
                if (uid == "A.test_1") await lastStarted.Task.WaitAsync(waitBudget);
            }
        };

        var supervisor = new Supervisor(factory) { Distribution = Distribution.Lazy, MaxParallelWorkers = 2 };
        supervisor.AddObserver(observer);

        var results = await supervisor.Run().WaitAsync(waitBudget);

        results.CleanPasses.Count.ShouldBe(6);

        var lanes = factory.RunningWorkers.OrderBy(w => w.Launch.Lane).ToList();
        lanes.Count.ShouldBe(2);
        lanes[0].Runs.Select(classOf).ShouldBe(["A"]);
        lanes[1].Runs.Select(classOf).ShouldBe(["B", "C", "D"]);

        // A finished last...
        observer.Finished.IndexOf("A.test_3").ShouldBeGreaterThan(observer.Finished.IndexOf("D.test_1"));

        // ...and is still recorded first, because it left the queue first.
        observer.Recorded.ShouldBe(["A.test_1", "A.test_2", "A.test_3", "B.test_1", "C.test_1", "D.test_1"]);

        // The report's lanes are what each lane pulled.
        results.LanePlan.Select(l => l.Partitions).ShouldBe([["A"], ["B", "C", "D"]]);
        results.LaneElapsed.Keys.Order().ShouldBe([0, 1]);
    }

    [Fact]
    public async Task a_same_process_retry_returns_to_the_lane_that_pulled_its_class()
    {
        var lastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tests = classes(("A", 3), ("C", 1), ("D", 1))
            .Append(FakeWorkerFactory.InClass("B", "test_1", "Retry=2", "RetryWarm=true"))
            .ToList();

        var factory = new FakeWorkerFactory
        {
            Tests = tests,
            Outcome = (uid, attempt, _) => uid == "B.test_1" && attempt == 1 ? WorkerTestState.Failed : WorkerTestState.Passed,
            HoldAfterStart = async (uid, _) =>
            {
                if (uid == "D.test_1") lastStarted.TrySetResult();
                if (uid == "A.test_1") await lastStarted.Task.WaitAsync(waitBudget);
            }
        };

        var results = await new Supervisor(factory)
        {
            Distribution = Distribution.Lazy,
            MaxParallelWorkers = 2,
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 }
        }.Run().WaitAsync(waitBudget);

        results.Tests.Single(t => t.Uid == "B.test_1").Outcome.ShouldBe(RunOutcome.PassOnRetry);

        // B was pulled by the lane that was not held in A, and its retry went back to that worker.
        var pulledB = factory.RunningWorkers.Single(w => w.Runs.Take(w.Runs.Count - 1).Any(r => r!.Contains("B.test_1")));
        pulledB.Runs[^1].ShouldBe(["B.test_1"]);
        pulledB.Runs.Select(classOf).ShouldNotContain("A");
        factory.RunningWorkers.Count.ShouldBe(2);
    }

    [Fact]
    public async Task a_lane_whose_worker_dies_relaunches_and_keeps_pulling()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = classes(("A", 2), ("B", 1), ("C", 1)),
            // Worker 1 is the first lane worker (0 is discovery): it dies on its first class.
            Fault = worker => worker.Index == 1 ? "worker died (exit code 70)" : null,
            Outcome = (_, _, worker) => worker.Index == 1 ? null : WorkerTestState.Passed
        };

        var results = await new Supervisor(factory) { Distribution = Distribution.Lazy }.Run();

        results.Indeterminate.Count.ShouldBe(2);
        results.Indeterminate.ShouldAllBe(t => t.Uid.StartsWith("A."));
        results.WorkerFaults.ShouldHaveSingleItem().ShouldContain("exit code 70");
        results.CleanPasses.Select(t => t.Uid).Order().ShouldBe(["B.test_1", "C.test_1"]);

        var lanes = factory.RunningWorkers.Where(w => w.Launch.Purpose == WorkerPurpose.Lane).ToList();
        lanes.Count.ShouldBe(2);
        lanes[0].Runs.Select(classOf).ShouldBe(["A"]);
        lanes[1].Runs.Select(classOf).ShouldBe(["B", "C"]);
        lanes.ShouldAllBe(w => w.Launch.Lane == 0);
    }

    [Fact]
    public async Task a_stall_kill_takes_only_its_own_class_and_the_lane_keeps_pulling_on_a_fresh_worker()
    {
        var time = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var factory = new FakeWorkerFactory
        {
            Tests = classes(("Wedge", 3), ("Other", 1)),
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (uid, worker) =>
            {
                if (uid != "Wedge.test_2" || worker.Index != 1) return Task.CompletedTask;
                started.TrySetResult();
                return release.Task;
            }
        };

        var supervisor = new Supervisor(factory)
        {
            Distribution = Distribution.Lazy,
            Time = time,
            StallThreshold = TimeSpan.FromSeconds(30),
            StallAction = StallAction.KillAndRetry
        };

        var run = supervisor.Run();
        await started.Task.WaitAsync(waitBudget);
        time.Advance(TimeSpan.FromSeconds(31));
        var results = await run.WaitAsync(waitBudget);

        results.ExitCode.ShouldBe(0, RunReport.ToText(results));
        results.StallKills.ShouldHaveSingleItem().Uid.ShouldBe("Wedge.test_2");
        results.WorkerFaults.ShouldBeEmpty();

        // The wedged test retried alone; its class-mate the kill cut off resumed in its lane.
        var wedged = results.Tests.Single(t => t.Uid == "Wedge.test_2");
        wedged.Attempts[0].StallInduced.ShouldBeTrue();
        wedged.Attempts[1].Placement.ShouldBe(AttemptPlacement.IsolatedProcess);
        var mate = results.Tests.Single(t => t.Uid == "Wedge.test_3");
        mate.Attempts[1].Placement.ShouldBe(AttemptPlacement.SameProcess);

        // The next class was never a batch-mate: it ran once, on a worker launched after the kill.
        var other = results.Tests.Single(t => t.Uid == "Other.test_1");
        other.AttemptCount.ShouldBe(1);
        factory.Launched.Single(w => w.Runs.Any(r => r!.Contains("Other.test_1"))).Index.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task a_heal_stops_the_lanes_pulling_and_the_queue_resumes_on_fresh_workers_after_it()
    {
        var time = new FakeTimeProvider();
        var known = TimeSpan.FromSeconds(2);
        var degraded = true;
        var heldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var slow = Enumerable.Range(1, 6).Select(i => FakeWorkerFactory.InClass("Slow", $"t{i}")).ToList();
        var held = FakeWorkerFactory.InClass("Held", "in_flight");
        var later = FakeWorkerFactory.InClass("Later", "after_the_heal");
        var tests = slow.Append(held).Append(later).ToList();

        var durations = slow.Append(later).ToDictionary(t => t.DisplayName, _ => known, StringComparer.Ordinal);
        durations[held.DisplayName] = TimeSpan.FromSeconds(10); // second in the queue, behind Slow

        var factory = new FakeWorkerFactory
        {
            Tests = tests,
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = async (uid, worker) =>
            {
                if (uid == held.Uid)
                {
                    if (worker.Launch.Purpose != WorkerPurpose.Lane) return;
                    heldStarted.TrySetResult();
                    await hold.Task;
                    return;
                }

                await heldStarted.Task.WaitAsync(waitBudget);
                time.Advance(degraded && worker.Launch.Purpose == WorkerPurpose.Lane ? TimeSpan.FromSeconds(60) : known);
            }
        };

        var heals = 0;
        var supervisor = new Supervisor(factory)
        {
            Distribution = Distribution.Lazy,
            Time = time,
            MaxParallelWorkers = 2,
            KnownTestDurations = durations,
            DegradationWindow = 3,
            OnDegraded = (_, _) =>
            {
                Interlocked.Increment(ref heals);
                degraded = false;
                return Task.FromResult(DegradationResponse.HealedTracks(new HealedTrack("sqlserver", 1)));
            }
        };

        var results = await supervisor.Run().WaitAsync(waitBudget);

        results.ExitCode.ShouldBe(0, RunReport.ToText(results));
        heals.ShouldBe(1);
        results.Tests.ShouldAllBe(t => t.Final.Succeeded);
        results.WorkerFaults.ShouldBeEmpty();

        // In flight when the run degraded: retried alone.
        var inFlight = results.Tests.Single(t => t.Uid == held.Uid);
        inFlight.Attempts[0].HealInduced.ShouldBeTrue();
        inFlight.Attempts[1].Placement.ShouldBe(AttemptPlacement.IsolatedProcess);

        // Slow's tests the kill cut off resumed in their lane.
        results.Tests.Where(t => t.Uid.StartsWith("Slow.") && t.AttemptCount == 2)
            .ShouldAllBe(t => t.Attempts[1].Placement == AttemptPlacement.SameProcess);

        // Later was still in the queue: nobody pulled it until the heal was over, so it ran once,
        // against the healed service, on a worker launched after the heal.
        var afterHeal = results.Tests.Single(t => t.Uid == later.Uid);
        afterHeal.AttemptCount.ShouldBe(1);
        afterHeal.Attempts[0].HealInduced.ShouldBeFalse();
        var ranLater = factory.Launched.Single(w => w.Runs.Any(r => r!.Contains(later.Uid)));
        ranLater.Runs[0].ShouldBe([later.Uid]);
        ranLater.Index.ShouldBeGreaterThan(2);
    }

    [Fact]
    public async Task a_random_lazy_queue_runs_the_classes_in_the_seeds_order()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = classes(("A", 2), ("B", 2), ("C", 2), ("D", 2), ("E", 2)),
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var results = await new Supervisor(factory)
        {
            Distribution = Distribution.Lazy, Order = RunOrder.Random, Seed = 42
        }.Run();

        results.CleanPasses.Count.ShouldBe(10);

        // The same order the eager random plan pins for seed 42 (RandomOrderTests).
        factory.RunningWorkers.ShouldHaveSingleItem().Runs.Select(classOf).ShouldBe(["E", "A", "B", "C", "D"]);
    }

    [Fact]
    public async Task the_report_says_the_lanes_were_pulled_not_planned()
    {
        var lastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeWorkerFactory
        {
            Tests = classes(("A", 2), ("B", 1), ("C", 1)),
            Outcome = (_, _, _) => WorkerTestState.Passed,
            Duration = (_, _) => TimeSpan.FromMilliseconds(10),
            HoldAfterStart = async (uid, _) =>
            {
                if (uid == "C.test_1") lastStarted.TrySetResult();
                if (uid == "A.test_1") await lastStarted.Task.WaitAsync(waitBudget);
            }
        };

        var results = await new Supervisor(factory) { Distribution = Distribution.Lazy, MaxParallelWorkers = 2 }
            .Run().WaitAsync(waitBudget);

        var text = RunReport.ToText(results);
        text.ShouldContain(RunReport.DistributionLine);
        text.ShouldContain("lanes as pulled (distribution: lazy, nothing planned)");
        text.ShouldNotContain("predicted");

        var json = JsonDocument.Parse(RunReport.ToJson(results)).RootElement;
        json.GetProperty("distribution").GetString().ShouldBe("lazy");
    }

    [Fact]
    public async Task eager_stays_the_default_and_says_so()
    {
        var factory = new FakeWorkerFactory { Tests = classes(("A", 2), ("B", 1)), Outcome = (_, _, _) => WorkerTestState.Passed };

        var results = await new Supervisor(factory).Run();

        results.Distribution.ShouldBe(Distribution.Eager);
        factory.RunningWorkers.ShouldHaveSingleItem().Runs.ShouldHaveSingleItem()!.Count.ShouldBe(3);
        RunReport.ToText(results).ShouldNotContain("Distribution:");
        JsonDocument.Parse(RunReport.ToJson(results)).RootElement.GetProperty("distribution").GetString().ShouldBe("eager");
    }
}
