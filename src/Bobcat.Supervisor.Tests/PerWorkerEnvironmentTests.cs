using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Per-worker environment: the seam that lets a parallel run point each worker at its own
/// database. Class-level partitioning keeps a class in one process, but two <em>different</em>
/// classes sharing a schema can still land in different workers — so the isolation has to be the
/// database, and naming it is a per-process environment decision.
/// </summary>
public class PerWorkerEnvironmentTests
{
    private static IReadOnlyList<WorkerTest> classes(params (string Name, int Count)[] classes)
        => classes
            .SelectMany(c => Enumerable.Range(1, c.Count).Select(i => FakeWorkerFactory.InClass(c.Name, $"test_{i}")))
            .ToList();

    [Fact]
    public async Task each_lane_is_launched_with_its_own_lane_number()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = classes(("A", 3), ("B", 3), ("C", 3), ("D", 3)),
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await new Supervisor(factory) { MaxParallelWorkers = 4 }.Run();

        var lanes = factory.RunningWorkers.Select(w => w.Launch.Lane).OrderBy(l => l).ToList();

        lanes.ShouldBe([0, 1, 2, 3]);
        factory.RunningWorkers.ShouldAllBe(w => w.Launch.Purpose == WorkerPurpose.Lane);
    }

    [Fact]
    public async Task lane_numbers_are_bounded_by_the_worker_count_not_the_process_count()
    {
        // The number of databases a suite must provision should equal the workers it asked for,
        // never the number of processes the supervisor happened to start.
        var tests = classes(("A", 3), ("B", 3))
            .Append(FakeWorkerFactory.Test("Lonely.test", "Isolated=true"))
            .ToList();

        var factory = new FakeWorkerFactory
        {
            Tests = tests,
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await new Supervisor(factory) { MaxParallelWorkers = 2 }.Run();

        factory.Launched.ShouldAllBe(w => w.Launch.Lane < 2);
    }

    [Fact]
    public async Task discovery_and_isolated_runs_report_their_purpose()
    {
        // Purpose lets a caller treat them differently — a throwaway database for discovery, say
        // — without having to infer it from the lane number.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Lonely.test", "Isolated=true")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await new Supervisor(factory).Run();

        factory.Launched[0].Launch.Purpose.ShouldBe(WorkerPurpose.Discovery);
        factory.Launched.ShouldContain(w => w.Launch.Purpose == WorkerPurpose.Isolated);
    }

    [Fact]
    public async Task a_recycled_retry_is_distinguishable_from_an_ordinary_isolated_one()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("brokered", "RecycleOnRetry=rabbit", "Retry=2")],
            Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Failed
        };

        var supervisor = new Supervisor(factory)
        {
            RetryBudget = new Bobcat.Resilience.RetryBudget { MaxAttemptsPerTest = 2 }
        };
        supervisor.AddRecyclableResource(new StubRecyclable("rabbit"));

        await supervisor.Run();

        factory.Launched.ShouldContain(w => w.Launch.Purpose == WorkerPurpose.Recycled);
    }

    // ── MtpWorkerFactory's environment layering ─────────────────────────────

    [Fact]
    public void per_worker_environment_is_layered_over_the_shared_one()
    {
        var factory = new MtpWorkerFactory("worker", new Dictionary<string, string>
        {
            ["SHARED"] = "shared value",
            ["OVERRIDDEN"] = "from the shared environment"
        })
        {
            EnvironmentFor = worker => new Dictionary<string, string>
            {
                ["OVERRIDDEN"] = $"from lane {worker.Lane}",
                ["DATABASE"] = $"polecat_w{worker.Lane}"
            }
        };

        var environment = environmentOf(factory, new WorkerLaunchContext(2, WorkerPurpose.Lane));

        environment!["SHARED"].ShouldBe("shared value");
        environment["DATABASE"].ShouldBe("polecat_w2");

        // The lane's value wins — the shared environment is a baseline, not a ceiling.
        environment["OVERRIDDEN"].ShouldBe("from lane 2");
    }

    [Fact]
    public void a_factory_with_no_per_worker_environment_is_unchanged()
    {
        var shared = new Dictionary<string, string> { ["SHARED"] = "value" };
        var factory = new MtpWorkerFactory("worker", shared);

        environmentOf(factory, new WorkerLaunchContext(3, WorkerPurpose.Lane))
            .ShouldBe(shared);
    }

    [Fact]
    public void a_factory_with_only_a_per_worker_environment_needs_no_shared_one()
    {
        var factory = new MtpWorkerFactory("worker")
        {
            EnvironmentFor = worker => new Dictionary<string, string> { ["LANE"] = worker.Lane.ToString() }
        };

        environmentOf(factory, new WorkerLaunchContext(1, WorkerPurpose.Lane))!["LANE"].ShouldBe("1");
    }

    /// <summary>
    /// Reaches the private merge directly. Launching a real process to read its environment would
    /// make this an integration test of process startup rather than of the layering rule.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? environmentOf(
        MtpWorkerFactory factory, WorkerLaunchContext context)
        => (IReadOnlyDictionary<string, string>?)typeof(MtpWorkerFactory)
            .GetMethod("environmentFor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(factory, [context]);

    private sealed class StubRecyclable(string name) : Bobcat.Runtime.IRecyclableResource
    {
        public string Name { get; } = name;
        public Task Recycle(CancellationToken token = default) => Task.CompletedTask;
        public Task Start() => Task.CompletedTask;
        public Task ResetBetweenScenarios() => Task.CompletedTask;
        public ValueTask DisposeAsync() => default;
    }
}
