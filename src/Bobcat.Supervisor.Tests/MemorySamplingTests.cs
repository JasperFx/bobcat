using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issue #149 — RunTiming for RSS. The prompting case was a green run that grew its test host
/// 375 MB → 9334 MB with nothing able to say which tests grew it; these tests prove the
/// supervisor can, and that everything unmeasurable stays null rather than becoming a zero.
/// </summary>
public class MemorySamplingTests
{
    private const long MB = 1024 * 1024;
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

    [Fact]
    public async Task a_tests_retained_memory_is_the_rss_delta_across_its_attempt()
    {
        var rss = 100 * MB;
        var hold = new Hold();

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Hungry/allocates")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (_, _) => hold.Enter(),
            WorkingSet = _ => Volatile.Read(ref rss)
        };

        var supervisor = new Supervisor(factory)
        {
            Time = new FakeTimeProvider(),
            ResourceSampleInterval = TimeSpan.FromSeconds(15)
        };

        var run = supervisor.Run();
        await hold.Started;

        // The test "allocates" 400 MB while it runs.
        Volatile.Write(ref rss, 500 * MB);
        hold.Release();

        var results = await run.WaitAsync(waitBudget);

        var retained = results.TestMemory.ShouldHaveSingleItem();
        retained.Uid.ShouldBe("Hungry/allocates");
        retained.RetainedBytes.ShouldBe(400 * MB);

        var worker = results.WorkerMemory.ShouldHaveSingleItem();
        worker.FirstBytes.ShouldBe(100 * MB);
        worker.PeakBytes.ShouldBe(500 * MB);
        worker.GrowthBytes.ShouldBe(400 * MB);

        var text = RunReport.ToText(results);
        text.ShouldContain("Memory (peak worker RSS 500 MB):");
        text.ShouldContain("+400 MB Hungry/allocates");
    }

    [Fact]
    public async Task the_interval_catches_a_peak_that_comes_and_goes_mid_test()
    {
        // Boundary samples alone would report 100 → 200 and miss the 900 MB balloon — the
        // whole reason the knob is an interval and not just attempt brackets.
        var time = new FakeTimeProvider();
        var rss = 100 * MB;
        var hold = new Hold();

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Hungry/balloons")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (_, _) => hold.Enter(),
            WorkingSet = _ => Volatile.Read(ref rss)
        };

        var supervisor = new Supervisor(factory)
        {
            Time = time,
            ResourceSampleInterval = TimeSpan.FromSeconds(15)
        };

        var run = supervisor.Run();
        await hold.Started;

        Volatile.Write(ref rss, 900 * MB);
        time.Advance(TimeSpan.FromSeconds(15));

        Volatile.Write(ref rss, 200 * MB);
        hold.Release();

        var results = await run.WaitAsync(waitBudget);

        var worker = results.WorkerMemory.ShouldHaveSingleItem();
        worker.PeakBytes.ShouldBe(900 * MB);
        worker.LastBytes.ShouldBe(200 * MB);
        results.TestMemory.ShouldHaveSingleItem().RetainedBytes.ShouldBe(100 * MB);
    }

    [Fact]
    public void overlapping_attempts_in_one_process_are_measured_but_never_attributed()
    {
        // xUnit's in-process parallelism means one worker can have several tests in flight.
        // The delta then has no single owner, and a wrong attribution is worse than a declared
        // gap — including for the test that started alone and was then joined.
        var sampler = new MemorySampler(new FakeTimeProvider());
        var rss = 100 * MB;
        var worker = new StubClient(() => Volatile.Read(ref rss));
        var launch = new WorkerLaunchContext(0, WorkerPurpose.Lane);

        sampler.Track(worker, launch);

        sampler.Apply(worker, new WorkerTestUpdate("a", "a", "in-progress"));
        sampler.Apply(worker, new WorkerTestUpdate("b", "b", "in-progress")); // joins: poisons both

        rss = 900 * MB;
        sampler.Apply(worker, new WorkerTestUpdate("a", "a", "passed") { State = WorkerTestState.Passed });
        sampler.Apply(worker, new WorkerTestUpdate("b", "b", "passed") { State = WorkerTestState.Passed });

        sampler.Tests.Count.ShouldBe(2);
        sampler.Tests.ShouldAllBe(test => test.RetainedBytes == null);

        // The worker's own story is unaffected — only attribution is voided.
        sampler.Workers.ShouldHaveSingleItem().PeakBytes.ShouldBe(900 * MB);
    }

    [Fact]
    public async Task an_unmeasurable_worker_reports_nothing_and_the_json_says_null()
    {
        // The default fake models tUnit-style erasure: sampling is on, but the client cannot
        // measure. Everything stays absent or null — zero-filling would make every other
        // figure quietly wrong.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Quick/passes")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = new Supervisor(factory)
        {
            Time = new FakeTimeProvider(),
            ResourceSampleInterval = TimeSpan.FromSeconds(15)
        };

        var results = await supervisor.Run().WaitAsync(waitBudget);

        results.WorkerMemory.ShouldBeEmpty();
        results.TestMemory.ShouldBeEmpty();
        RunReport.ToText(results).ShouldNotContain("Memory (");
        RunReport.ToJson(results).ShouldContain("\"peakBytes\": null");
    }

    [Fact]
    public async Task sampling_off_means_not_a_single_sample_is_taken()
    {
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Quick/passes")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            WorkingSet = _ => 100 * MB
        };

        var supervisor = new Supervisor(factory) { Time = new FakeTimeProvider() };

        var results = await supervisor.Run().WaitAsync(waitBudget);

        factory.SamplesTaken.ShouldBe(0);
        results.WorkerMemory.ShouldBeEmpty();
        results.TestMemory.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_database_target_is_sampled_beside_the_workers_and_reported_with_them()
    {
        // bobcat#702: on a hosted runner the database container is often the larger consumer,
        // and what kills the runner is the total.
        var time = new FakeTimeProvider();
        var hold = new Hold();
        var database = new FakeTarget("postgres", 300 * MB);

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Heavy/queries")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (_, _) => hold.Enter(),
            WorkingSet = _ => 100 * MB
        };

        var supervisor = new Supervisor(factory)
        {
            Time = time,
            ResourceSampleInterval = TimeSpan.FromSeconds(15)
        }.AddMemorySampleTarget(database);

        var run = supervisor.Run();
        await hold.Started;

        database.Bytes = 2000 * MB;
        time.Advance(TimeSpan.FromSeconds(15));
        await database.WaitForSamples(2).WaitAsync(waitBudget);

        database.Bytes = 1200 * MB;
        hold.Release();

        var results = await run.WaitAsync(waitBudget);

        var target = results.TargetMemory.ShouldHaveSingleItem();
        target.Name.ShouldBe("postgres");
        target.FirstBytes.ShouldBe(300 * MB);
        target.PeakBytes.ShouldBe(2000 * MB);
        target.LastBytes.ShouldBe(1200 * MB);
        target.GrowthBytes.ShouldBe(900 * MB);

        // Beside the worker, not instead of it.
        results.WorkerMemory.ShouldHaveSingleItem().PeakBytes.ShouldBe(100 * MB);
        RunResources.For(results).Targets.ShouldHaveSingleItem().ShouldBe(target);

        var text = RunReport.ToText(results);
        text.ShouldContain("Memory (peak worker RSS 100 MB):");
        text.ShouldContain("• postgres: 300 MB → 2000 MB peak (+900 MB)");

        var json = RunReport.ToJson(results);
        json.ShouldContain("\"name\": \"postgres\"");
        json.ShouldContain($"\"peakBytes\": {2000 * MB}");
    }

    [Fact]
    public async Task a_target_that_cannot_be_measured_is_reported_as_not_measured_and_null()
    {
        // docker not on PATH, the container gone: the target is named, its figures are null.
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Quick/passes")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = new Supervisor(factory)
        {
            Time = new FakeTimeProvider(),
            ResourceSampleInterval = TimeSpan.FromSeconds(15)
        }
            .AddMemorySampleTarget(new FakeTarget("postgres", null))
            .AddMemorySampleTarget(new ThrowingTarget("sqlserver"));

        var results = await supervisor.Run().WaitAsync(waitBudget);

        results.TargetMemory.Select(t => t.Name).ShouldBe(["postgres", "sqlserver"]);
        results.TargetMemory.ShouldAllBe(t => !t.IsMeasured && t.PeakBytes == null && t.GrowthBytes == null);
        RunResources.For(results).IsMeasured.ShouldBeFalse();

        var text = RunReport.ToText(results);
        text.ShouldContain("Memory (no worker measured):");
        text.ShouldContain("• postgres: not measured");
        text.ShouldContain("• sqlserver: not measured");

        var json = RunReport.ToJson(results);
        json.ShouldContain("\"name\": \"sqlserver\"");
        json.ShouldNotContain("\"peakBytes\": 0");
    }

    [Fact]
    public async Task a_target_is_never_sampled_while_sampling_is_off()
    {
        var database = new FakeTarget("postgres", 300 * MB);
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Quick/passes")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = new Supervisor(factory) { Time = new FakeTimeProvider() }
            .AddMemorySampleTarget(database);

        var results = await supervisor.Run().WaitAsync(waitBudget);

        database.SamplesTaken.ShouldBe(0);
        results.TargetMemory.ShouldBeEmpty();
        RunReport.ToText(results).ShouldNotContain("Memory (");
    }

    private sealed class FakeTarget(string name, long? bytes) : IMemorySampleTarget
    {
        private int _samples;
        private readonly List<(int Count, TaskCompletionSource Done)> _waiters = [];

        public string Name { get; } = name;
        public long? Bytes { get; set; } = bytes;
        public int SamplesTaken => Volatile.Read(ref _samples);

        public Task<long?> SampleBytes(CancellationToken ct)
        {
            var value = Bytes;
            var count = Interlocked.Increment(ref _samples);
            lock (_waiters)
            {
                foreach (var waiter in _waiters.Where(w => count >= w.Count)) waiter.Done.TrySetResult();
            }

            return Task.FromResult(value);
        }

        public Task WaitForSamples(int count)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiters)
            {
                if (SamplesTaken >= count) return Task.CompletedTask;
                _waiters.Add((count, done));
            }

            return done.Task;
        }
    }

    private sealed class ThrowingTarget(string name) : IMemorySampleTarget
    {
        public string Name { get; } = name;
        public Task<long?> SampleBytes(CancellationToken ct) => throw new InvalidOperationException("no docker");
    }

    private sealed class StubClient(Func<long?> workingSet) : IWorkerClient
    {
        public long? SampleWorkingSet() => workingSet();

        public Task<IReadOnlyList<WorkerTest>> Discover(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkerTest>>([]);

        public Task<WorkerRunResult> Run(IReadOnlyList<string>? uids = null, CancellationToken ct = default)
            => Task.FromResult(new WorkerRunResult([]));

        public ValueTask DisposeAsync() => default;
    }
}
