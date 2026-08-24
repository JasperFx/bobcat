using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issues #145/#148 — the supervisor's live view of what is in flight, surfaced as stall
/// reports and heartbeats. Driven entirely by a fake clock: a hung test is a worker held on a
/// TaskCompletionSource, and time only moves when the test says so.
/// </summary>
public class StallAndHeartbeatTests
{
    private static readonly TimeSpan waitBudget = TimeSpan.FromSeconds(10);

    private sealed class RecordingObserver : ISupervisorObserver
    {
        public List<(WorkerLaunchContext Worker, string Uid, string DisplayName, TimeSpan InFlight)> Stalled { get; } = [];
        public List<SupervisorHeartbeat> Heartbeats { get; } = [];

        public void TestStalled(WorkerLaunchContext worker, string uid, string displayName, TimeSpan inFlight)
        {
            lock (Stalled) Stalled.Add((worker, uid, displayName, inFlight));
        }

        public void Heartbeat(SupervisorHeartbeat heartbeat)
        {
            lock (Heartbeats) Heartbeats.Add(heartbeat);
        }
    }

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
    public async Task a_hung_test_is_named_once_it_crosses_the_stall_threshold()
    {
        // The whole point of #145: wolverine's CIMarten log went 18m33s from the batch plan to
        // the CI cap with nothing in between, and could not name the wedged test.
        var time = new FakeTimeProvider();
        var observer = new RecordingObserver();
        var log = new List<string>();
        var hold = new Hold();

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Slow/hangs")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (uid, _) => uid == "Slow/hangs" ? hold.Enter() : Task.CompletedTask
        };

        var supervisor = new Supervisor(factory)
        {
            Time = time,
            StallThreshold = TimeSpan.FromSeconds(30),
            Log = line => { lock (log) log.Add(line); }
        };
        supervisor.AddObserver(observer);

        var run = supervisor.Run();
        await hold.Started;

        time.Advance(TimeSpan.FromSeconds(31));

        var stalled = observer.Stalled.ShouldHaveSingleItem();
        stalled.Uid.ShouldBe("Slow/hangs");
        stalled.DisplayName.ShouldBe("Slow/hangs");
        stalled.InFlight.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30));
        stalled.Worker.Purpose.ShouldBe(WorkerPurpose.Lane);

        lock (log) log.ShouldContain(line => line.StartsWith("STALLED: Slow/hangs"));

        // Once per attempt: the heartbeat is the continuous view, the stall is the crossing.
        time.Advance(TimeSpan.FromMinutes(5));
        observer.Stalled.Count.ShouldBe(1);

        hold.Release();
        var results = await run.WaitAsync(waitBudget);

        // The test finished green — and still exceeded its budget, so the fact survives into
        // the results and both report views.
        results.ExitCode.ShouldBe(0);
        results.StalledTests.ShouldHaveSingleItem().Uid.ShouldBe("Slow/hangs");
        RunReport.ToText(results).ShouldContain("Stalled (in flight past the stall threshold):");
        RunReport.ToJson(results).ShouldContain("\"Slow/hangs\"");
    }

    [Fact]
    public async Task the_heartbeat_reports_progress_and_the_climbing_longest_running_figure()
    {
        var time = new FakeTimeProvider();
        var observer = new RecordingObserver();
        var log = new List<string>();
        var hold = new Hold();

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Slow/hangs")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (_, _) => hold.Enter()
        };

        var supervisor = new Supervisor(factory)
        {
            Time = time,
            HeartbeatInterval = TimeSpan.FromSeconds(10),
            Log = line => { lock (log) log.Add(line); }
        };
        supervisor.AddObserver(observer);

        var run = supervisor.Run();
        await hold.Started;

        time.Advance(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(10));

        observer.Heartbeats.Count.ShouldBe(2);

        var first = observer.Heartbeats[0];
        first.Completed.ShouldBe(0);
        first.Total.ShouldBe(1);
        first.InFlight.ShouldHaveSingleItem().Uid.ShouldBe("Slow/hangs");

        // The reader's signal for a stuck run: the longest-running figure keeps climbing.
        observer.Heartbeats[1].LongestRunning!.InFlight
            .ShouldBeGreaterThan(first.LongestRunning!.InFlight);

        lock (log)
        {
            log.ShouldContain("10s — 0/1 done, 1 in flight (lane 0), longest running: Slow/hangs (10s)");
            log.ShouldContain("20s — 0/1 done, 1 in flight (lane 0), longest running: Slow/hangs (20s)");
        }

        hold.Release();
        (await run.WaitAsync(waitBudget)).ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task a_per_test_budget_wins_over_the_blanket_threshold()
    {
        // An integration suite budgets differently by trait — a broker test is allowed minutes
        // a unit test is not.
        var time = new FakeTimeProvider();
        var observer = new RecordingObserver();
        var holds = new Dictionary<string, Hold>
        {
            ["Alpha/hangs"] = new(),
            ["Beta/hangs"] = new()
        };

        var factory = new FakeWorkerFactory
        {
            Tests =
            [
                FakeWorkerFactory.Test("Alpha/hangs", "ShortBudget"),
                FakeWorkerFactory.Test("Beta/hangs")
            ],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (uid, _) => holds[uid].Enter()
        };

        var supervisor = new Supervisor(factory)
        {
            Time = time,
            MaxParallelWorkers = 2,
            StallThresholdFor = test => test.Traits.ContainsKey("ShortBudget")
                ? TimeSpan.FromSeconds(5)
                : TimeSpan.FromSeconds(60)
        };
        supervisor.AddObserver(observer);

        var run = supervisor.Run();
        await holds["Alpha/hangs"].Started;
        await holds["Beta/hangs"].Started;

        time.Advance(TimeSpan.FromSeconds(10));

        observer.Stalled.ShouldHaveSingleItem().Uid.ShouldBe("Alpha/hangs");

        foreach (var hold in holds.Values) hold.Release();
        var results = await run.WaitAsync(waitBudget);

        results.StalledTests.ShouldHaveSingleItem().Uid.ShouldBe("Alpha/hangs");
    }

    [Fact]
    public async Task a_retry_gets_a_fresh_stall_clock_and_may_stall_again()
    {
        // A new attempt is a new wait: its clock and its once-per-attempt reporting both reset.
        var time = new FakeTimeProvider();
        var observer = new RecordingObserver();
        var holds = new[] { new Hold(), new Hold() };
        var attempts = 0;

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Slow/hangs", "Retry=2")],
            Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Failed,
            HoldAfterStart = (_, _) => holds[Interlocked.Increment(ref attempts) - 1].Enter()
        };

        var supervisor = new Supervisor(factory)
        {
            Time = time,
            StallThreshold = TimeSpan.FromSeconds(30),
            RetryBudget = new Resilience.RetryBudget { MaxAttemptsPerTest = 2 }
        };
        supervisor.AddObserver(observer);

        var run = supervisor.Run();

        await holds[0].Started;
        time.Advance(TimeSpan.FromSeconds(31));
        observer.Stalled.Count.ShouldBe(1);
        holds[0].Release();

        await holds[1].Started;
        time.Advance(TimeSpan.FromSeconds(31));
        observer.Stalled.Count.ShouldBe(2);
        holds[1].Release();

        var results = await run.WaitAsync(waitBudget);
        results.StalledTests.Count.ShouldBe(2);
        results.PassedOnRetry.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task nothing_is_scheduled_when_neither_knob_is_configured()
    {
        // Off by default is the contract, and "off" means no timer exists at all — not a timer
        // that wakes up and decides to do nothing.
        var time = new FakeTimeProvider();
        var observer = new RecordingObserver();
        var hold = new Hold();

        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("Slow/hangs")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (_, _) => hold.Enter()
        };

        var supervisor = new Supervisor(factory) { Time = time };
        supervisor.AddObserver(observer);

        var run = supervisor.Run();
        await hold.Started;

        time.Advance(TimeSpan.FromMinutes(30));

        time.TimersCreated.ShouldBe(0);
        observer.Stalled.ShouldBeEmpty();
        observer.Heartbeats.ShouldBeEmpty();

        hold.Release();
        var results = await run.WaitAsync(waitBudget);
        results.StalledTests.ShouldBeEmpty();
    }
}
