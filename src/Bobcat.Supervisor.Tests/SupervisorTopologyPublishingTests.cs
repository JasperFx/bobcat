using Bobcat.Monitoring;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issue #84, the wire half: lane topology, recycles and worker deaths streamed live to the
/// monitor as they happen, not reported once the run is over.
/// </summary>
public class SupervisorTopologyPublishingTests
{
    private sealed class RecordingSink : IMonitorEventSink
    {
        private readonly List<MonitorEvent> _events = [];

        public void Post(MonitorEvent @event)
        {
            lock (_events) _events.Add(@event);
        }

        public IReadOnlyList<MonitorEvent> Events
        {
            get { lock (_events) return _events.ToArray(); }
        }
    }

    private sealed class StubRecyclable(string name) : Bobcat.Runtime.IRecyclableResource
    {
        public string Name { get; } = name;
        public Task Recycle(CancellationToken token = default) => Task.CompletedTask;
        public Task Start() => Task.CompletedTask;
        public Task ResetBetweenScenarios() => Task.CompletedTask;
        public ValueTask DisposeAsync() => default;
    }

    [Fact]
    public async Task every_lane_announces_the_tests_it_was_handed_and_when_it_finished_them()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests =
            [
                FakeWorkerFactory.InClass("Alpha", "one"),
                FakeWorkerFactory.InClass("Alpha", "two"),
                FakeWorkerFactory.InClass("Beta", "three")
            ],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = new Supervisor(factory)
        {
            MaxParallelWorkers = 2,
            PublishToMonitor = true,
            MonitorSink = sink
        };

        await supervisor.Run();

        var runId = sink.Events.OfType<RunStarted>().Single().RunId;

        var started = sink.Events.OfType<LaneStarted>().OrderBy(l => l.Lane).ToList();
        started.Select(l => l.Lane).ShouldBe([0, 1]);
        // Partitioned by class, so the two Alpha tests ride one lane together.
        started.SelectMany(l => l.Uids).OrderBy(u => u).ShouldBe(["Alpha.one", "Alpha.two", "Beta.three"]);
        started.Single(l => l.Uids.Contains("Alpha.one")).Uids.ShouldContain("Alpha.two");
        started.ShouldAllBe(l => l.RunId == runId);

        var finished = sink.Events.OfType<LaneFinished>().OrderBy(l => l.Lane).ToList();
        finished.Select(l => l.Lane).ShouldBe([0, 1]);
        finished.ShouldAllBe(l => !l.Crashed);
        finished.Sum(l => l.Outcomes).ShouldBe(3);

        // Each lane starts before it finishes, on the supervisor's own clock.
        foreach (var lane in started)
        {
            finished.Single(f => f.Lane == lane.Lane).At.ShouldBeGreaterThanOrEqualTo(lane.At);
        }
    }

    [Fact]
    public async Task a_same_process_retry_starts_the_lane_again_with_only_the_retried_tests()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("flaky", "Retry=2"), FakeWorkerFactory.Test("clean")],
            Outcome = (uid, attempt, _) =>
                uid == "clean" || attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Failed
        };

        var supervisor = new Supervisor(factory)
        {
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 },
            PublishToMonitor = true,
            MonitorSink = sink
        };

        await supervisor.Run();

        var lanes = sink.Events.OfType<LaneStarted>().ToList();
        lanes.Count.ShouldBe(2);
        lanes[0].Lane.ShouldBe(0);
        lanes[0].Uids.OrderBy(u => u).ShouldBe(["clean", "flaky"]);
        // Back to the lane it ran in, carrying only what is being retried.
        lanes[1].Lane.ShouldBe(0);
        lanes[1].Uids.ShouldBe(["flaky"]);

        sink.Events.OfType<LaneFinished>().Count().ShouldBe(2);
    }

    [Fact]
    public async Task a_recycle_is_announced_and_the_recycled_retry_is_not_a_lane()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("broker", "RecycleOnRetry=rabbit,kafka", "Retry=2")],
            Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Failed
        };

        var supervisor = new Supervisor(factory)
        {
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 },
            PublishToMonitor = true,
            MonitorSink = sink
        };
        supervisor.AddRecyclableResource(new StubRecyclable("rabbit"));
        supervisor.AddRecyclableResource(new StubRecyclable("kafka"));

        await supervisor.Run();

        var runId = sink.Events.OfType<RunStarted>().Single().RunId;
        var recycled = sink.Events.OfType<ResourceRecycled>().ToList();
        recycled.Select(r => r.Resource).ShouldBe(["rabbit", "kafka"]);
        recycled.ShouldAllBe(r => r.RunId == runId);

        // The retry after the recycle runs alone in a throwaway process: one lane start for
        // the first pass, none for the retry.
        sink.Events.OfType<LaneStarted>().Count().ShouldBe(1);

        // Ordered on the wire: the retry was scheduled, then the resources recycled.
        var events = sink.Events.ToList();
        events.FindIndex(e => e is RetryScheduled).ShouldBeLessThan(events.FindIndex(e => e is ResourceRecycled));
    }

    [Fact]
    public async Task a_dead_lane_worker_is_announced_with_its_lane_exit_code_and_last_standard_error()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.InClass("Alpha", "one"), FakeWorkerFactory.InClass("Beta", "two")],
            // Lane 1's worker reports nothing and dies.
            Outcome = (_, _, worker) => worker.Launch.Lane == 1 ? null : WorkerTestState.Passed,
            Fault = w => w.Launch.Lane == 1 && w.Runs.Count > 0 ? "the worker exited with code 139" : null,
            FaultExitCode = 139,
            FaultStandardError = "Segmentation fault (core dumped)"
        };

        var supervisor = new Supervisor(factory)
        {
            MaxParallelWorkers = 2,
            PublishToMonitor = true,
            MonitorSink = sink
        };

        var results = await supervisor.Run();
        results.Indeterminate.Count.ShouldBe(1);

        var fault = sink.Events.OfType<WorkerFaulted>().Single();
        fault.Lane.ShouldBe(1);
        fault.Fault.ShouldBe("the worker exited with code 139");
        fault.ExitCode.ShouldBe(139);
        fault.StandardError.ShouldBe("Segmentation fault (core dumped)");
        fault.RunId.ShouldBe(sink.Events.OfType<RunStarted>().Single().RunId);

        // The lane that died says so, and the one that lived does not.
        sink.Events.OfType<LaneFinished>().Single(l => l.Lane == 1).Crashed.ShouldBeTrue();
        sink.Events.OfType<LaneFinished>().Single(l => l.Lane == 0).Crashed.ShouldBeFalse();

        // The sentence on the wire is the one the report collects — dashboard and report agree.
        results.WorkerFaults.ShouldBe([fault.Fault]);
    }

    [Fact]
    public async Task a_dead_isolated_worker_is_announced_with_no_lane()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("alone", "Isolated=true")],
            Outcome = (_, _, _) => null,
            Fault = w => w.Runs.Count > 0 ? "the worker exited with code 134" : null,
            FaultExitCode = 134
        };

        var supervisor = new Supervisor(factory) { PublishToMonitor = true, MonitorSink = sink };
        await supervisor.Run();

        var fault = sink.Events.OfType<WorkerFaulted>().Single();
        // Not a lane: a one-test process that was about to be thrown away anyway.
        fault.Lane.ShouldBeNull();
        fault.ExitCode.ShouldBe(134);
        fault.StandardError.ShouldBeNull();

        sink.Events.OfType<LaneStarted>().ShouldBeEmpty();
    }

    [Fact]
    public async Task an_observer_implementing_only_the_string_fault_callback_still_hears_the_structured_one()
    {
        // The structured WorkerFaulted(WorkerFault) forwards to WorkerFaulted(string) by default,
        // so an observer written against the original member keeps working unchanged.
        var faults = new List<string>();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("vanished")],
            Outcome = (_, _, _) => null,
            Fault = w => w.Runs.Count > 0 ? "the worker exited with code 1" : null
        };

        var supervisor = new Supervisor(factory);
        supervisor.AddObserver(new StringOnlyObserver(faults));

        await supervisor.Run();

        faults.ShouldBe(["the worker exited with code 1"]);
    }

    [Fact]
    public async Task nothing_about_the_topology_reaches_the_monitor_when_publishing_is_off()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a"), FakeWorkerFactory.Test("b")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = new Supervisor(factory) { MaxParallelWorkers = 2, MonitorSink = sink };
        await supervisor.Run();

        sink.Events.ShouldBeEmpty();
    }

    private sealed class StringOnlyObserver(List<string> faults) : ISupervisorObserver
    {
        public void WorkerFaulted(string fault) => faults.Add(fault);
    }

    // The observability cluster on the wire (issues #145/#146/#148/#149).

    [Fact]
    public async Task every_worker_launch_is_announced_with_its_purpose_and_pid()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            ProcessIdFor = index => 4200 + index
        };

        var supervisor = new Supervisor(factory) { PublishToMonitor = true, MonitorSink = sink };
        await supervisor.Run();

        // Discovery is NOT announced on the wire: it launches before the run bracket opens,
        // and run_started stays the stream's first event. Code observers still hear it.
        var started = sink.Events.OfType<Monitoring.WorkerStarted>().ShouldHaveSingleItem();
        started.Purpose.ShouldBe("Lane");
        started.Lane.ShouldBe(0);
        started.ProcessId.ShouldBe(4201);

        sink.Events.First().ShouldBeOfType<RunStarted>();
    }

    [Fact]
    public async Task a_stall_and_the_progress_heartbeat_reach_the_wire_with_the_memory_figure()
    {
        const long mb = 1024 * 1024;
        var time = new FakeTimeProvider();
        var sink = new RecordingSink();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Slow/hangs")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (_, _) =>
            {
                started.TrySetResult();
                return release.Task;
            },
            ProcessIdFor = index => 4200 + index,
            WorkingSet = _ => 500 * mb
        };

        var supervisor = new Supervisor(factory)
        {
            Time = time,
            StallThreshold = TimeSpan.FromSeconds(30),
            HeartbeatInterval = TimeSpan.FromSeconds(10),
            ResourceSampleInterval = TimeSpan.FromSeconds(15),
            PublishToMonitor = true,
            MonitorSink = sink
        };

        var run = supervisor.Run();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        time.Advance(TimeSpan.FromSeconds(31));

        var stalled = sink.Events.OfType<Monitoring.TestStalled>().ShouldHaveSingleItem();
        stalled.Uid.ShouldBe("Slow/hangs");
        stalled.DisplayName.ShouldBe("Slow/hangs");
        stalled.InFlightMs.ShouldBeGreaterThanOrEqualTo(30_000);
        stalled.Lane.ShouldBe(0);
        stalled.ProcessId.ShouldBe(4201);

        var progress = sink.Events.OfType<Monitoring.RunProgress>().ToList();
        progress.ShouldNotBeEmpty();
        var first = progress[0];
        first.Completed.ShouldBe(0);
        first.Total.ShouldBe(1);
        first.InFlight.ShouldBe(1);
        first.LongestRunningUid.ShouldBe("Slow/hangs");
        first.LongestRunningMs.ShouldNotBeNull();
        // The 375 MB → 9 GB story, live: memory sampling was on, so the peak rides along.
        first.PeakWorkerRssBytes.ShouldBe(500 * mb);

        release.TrySetResult();
        (await run.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task the_progress_heartbeat_reports_null_memory_when_sampling_is_off()
    {
        // Unmeasured is never zero — the wire rule too.
        var time = new FakeTimeProvider();
        var sink = new RecordingSink();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Slow/hangs")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (_, _) =>
            {
                started.TrySetResult();
                return release.Task;
            }
        };

        var supervisor = new Supervisor(factory)
        {
            Time = time,
            HeartbeatInterval = TimeSpan.FromSeconds(10),
            PublishToMonitor = true,
            MonitorSink = sink
        };

        var run = supervisor.Run();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        time.Advance(TimeSpan.FromSeconds(10));

        sink.Events.OfType<Monitoring.RunProgress>().ShouldHaveSingleItem().PeakWorkerRssBytes.ShouldBeNull();

        release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
