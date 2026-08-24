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
