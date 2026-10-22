using Bobcat.Monitoring;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#767 — the supervisor publishes <c>run_progress</c> while a run is going (planned,
/// finished, passed, failed, retried, workers busy, elapsed) at a bounded cadence, and the run
/// bracket's two ends go through the sink's terminal path so a burst can never drop them.
/// Driven on a <see cref="FakeTimeProvider"/>: the cadence is a property of the clock, and a test
/// that slept would prove nothing about it.
/// </summary>
public class SupervisorRunProgressPublishingTests
{
    private sealed class RecordingSink : IMonitorEventSink
    {
        private readonly List<MonitorEvent> _events = [];
        private readonly List<MonitorEvent> _terminal = [];

        public void Post(MonitorEvent @event)
        {
            lock (_events) _events.Add(@event);
        }

        public void PostTerminal(MonitorEvent @event)
        {
            lock (_events)
            {
                _events.Add(@event);
                _terminal.Add(@event);
            }
        }

        public IReadOnlyList<MonitorEvent> Events
        {
            get { lock (_events) return _events.ToArray(); }
        }

        /// <summary>What arrived through the never-dropped path.</summary>
        public IReadOnlyList<MonitorEvent> Terminal
        {
            get { lock (_events) return _terminal.ToArray(); }
        }

        public IReadOnlyList<RunProgress> Progress => Events.OfType<RunProgress>().ToList();
    }

    /// <summary>
    /// Moves the clock past the cadence on every callback, ahead of the publisher (observers are
    /// told in registration order and the publisher is told last), so every change posts and the
    /// counts can be read at each step.
    /// </summary>
    private sealed class ClockAdvancingObserver(FakeTimeProvider time) : ISupervisorObserver
    {
        public void TestUpdated(WorkerLaunchContext worker, WorkerTestUpdate update) => time.Advance(TimeSpan.FromSeconds(2));
        public void AttemptRecorded(string uid, SupervisorAttempt attempt) => time.Advance(TimeSpan.FromSeconds(2));
        public void RetryScheduled(string uid, int nextAttempt, Disposition disposition) => time.Advance(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task the_bracket_goes_through_the_terminal_path_and_nothing_follows_run_finished()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a"), FakeWorkerFactory.Test("b")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await new Supervisor(factory) { PublishToMonitor = true, MonitorSink = sink }.Run();

        sink.Terminal.Select(e => e.GetType()).ShouldBe([typeof(RunStarted), typeof(RunFinished)]);
        sink.Events[^1].ShouldBeOfType<RunFinished>();
        sink.Progress.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task the_counts_follow_the_run_and_a_retry_is_pending_rather_than_failed()
    {
        var time = new FakeTimeProvider();
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests =
            [
                FakeWorkerFactory.Test("clean"),
                FakeWorkerFactory.Test("flaky", "Retry=2"),
                FakeWorkerFactory.Test("broken")
            ],
            Outcome = (uid, attempt, _) => uid switch
            {
                "clean" => WorkerTestState.Passed,
                "flaky" => attempt == 1 ? WorkerTestState.Failed : WorkerTestState.Passed,
                _ => WorkerTestState.Failed
            }
        };

        var supervisor = new Supervisor(factory)
        {
            Time = time,
            MaxParallelWorkers = 1,
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 },
            PublishToMonitor = true,
            MonitorSink = sink
        };
        supervisor.AddObserver(new ClockAdvancingObserver(time));

        await supervisor.Run();

        var progress = sink.Progress;
        progress.ShouldNotBeEmpty();

        // Planned is the post-filter total run_started announced, and finished never exceeds it
        // however many attempts the flaky test took.
        progress.ShouldAllBe(p => p.Total == 3);
        progress.ShouldAllBe(p => p.Completed <= p.Total);
        progress.ShouldAllBe(p => p.Passed + p.Failed <= p.Completed);
        // One elapsed origin, so a latest-wins reader ordering by it is never rolled back.
        progress.Select(p => p.ElapsedMs).ShouldBeInOrder();

        // While the flaky test's retry was pending it was neither finished nor failed — the other
        // two were (one each way) — and it was already counted as retried: the state a bar shows
        // between the two attempts. (Its live verdict said Failed for a moment before the policy
        // spoke; a reader folding latest-wins keeps this one.)
        progress.ShouldContain(p => p.Retried == 1 && p.Completed == 2 && p.Passed == 1 && p.Failed == 1,
            string.Join("\n", progress));

        // Where the run ended up, as the last progress record saw it.
        var last = progress[^1];
        last.Completed.ShouldBe(3);
        last.Passed.ShouldBe(2);
        last.Failed.ShouldBe(1);
        last.Retried.ShouldBe(1);
        last.InFlight.ShouldBe(0);
        last.WorkersBusy.ShouldBe(0);

        // And it agrees with the terminal record's honest split.
        var finished = sink.Events.OfType<RunFinished>().Single();
        (finished.Passed + finished.PassedOnRetry).ShouldBe(last.Passed!.Value);
        finished.Failed.ShouldBe(last.Failed!.Value);
    }

    [Fact]
    public async Task progress_posts_at_most_once_an_interval_and_the_newest_state_still_lands()
    {
        var time = new FakeTimeProvider();
        var sink = new RecordingSink();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tests = Enumerable.Range(1, 20).Select(i => FakeWorkerFactory.Test($"t{i:00}")).ToList();
        var factory = new FakeWorkerFactory
        {
            Tests = tests,
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (uid, _) =>
            {
                if (uid != "t20") return Task.CompletedTask;
                lastStarted.TrySetResult();
                return release.Task;
            }
        };

        var supervisor = new Supervisor(factory)
        {
            Time = time,
            MaxParallelWorkers = 1,
            PublishToMonitor = true,
            MonitorSink = sink
        };

        var run = supervisor.Run();
        await lastStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Nineteen tests started and finished and the twentieth started, all inside one frozen
        // second: the first change posted at once, and everything after it is held for one
        // trailing post rather than forty more.
        var first = sink.Progress.ShouldHaveSingleItem();
        first.InFlight.ShouldBe(1);
        first.WorkersBusy.ShouldBe(1);
        first.Completed.ShouldBe(0);

        // The interval ends: the trailing post carries the newest state, not the state at the
        // moment it was armed.
        time.Advance(SupervisorRunPublisher.ProgressInterval);

        var progress = sink.Progress;
        progress.Count.ShouldBe(2);
        var trailing = progress[^1];
        trailing.Completed.ShouldBe(19);
        trailing.Passed.ShouldBe(19);
        trailing.Failed.ShouldBe(0);
        trailing.InFlight.ShouldBe(1);
        trailing.WorkersBusy.ShouldBe(1);
        trailing.LongestRunningUid.ShouldBe("t20");
        trailing.ElapsedMs.ShouldBe((long)SupervisorRunPublisher.ProgressInterval.TotalMilliseconds);

        // Nothing is armed once the trailing post has fired; the next change starts a new window.
        time.Advance(TimeSpan.FromMinutes(1));
        sink.Progress.Count.ShouldBe(2);

        release.TrySetResult();
        (await run.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode.ShouldBe(0);

        // run_finished closed the stream; a trailing post never follows it.
        sink.Events[^1].ShouldBeOfType<RunFinished>();
    }

    [Fact]
    public async Task a_change_in_the_failed_count_posts_immediately_inside_the_interval()
    {
        var time = new FakeTimeProvider();
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a"), FakeWorkerFactory.Test("b"), FakeWorkerFactory.Test("c")],
            Outcome = (uid, _, _) => uid == "b" ? WorkerTestState.Failed : WorkerTestState.Passed
        };

        var supervisor = new Supervisor(factory)
        {
            Time = time,
            MaxParallelWorkers = 1,
            PublishToMonitor = true,
            MonitorSink = sink
        };

        await supervisor.Run();

        // The clock never moved, so the cadence allowed exactly one change-driven post — the
        // first — and held the rest. The failure is the exception: a bar goes red the moment the
        // supervisor knows, not a second later.
        var progress = sink.Progress;
        progress.Count.ShouldBe(2);
        progress[0].Failed.ShouldBe(0);
        var red = progress[1];
        red.Failed.ShouldBe(1);
        red.Completed.ShouldBe(2);
        red.Passed.ShouldBe(1);
    }

    [Fact]
    public async Task workers_busy_counts_distinct_workers_with_a_test_in_flight()
    {
        var time = new FakeTimeProvider();
        var sink = new RecordingSink();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothStarted = new CountdownEvent(2);

        var factory = new FakeWorkerFactory
        {
            // Two classes, so the plan gives them two lanes.
            Tests = [FakeWorkerFactory.InClass("Alpha", "one"), FakeWorkerFactory.InClass("Beta", "two")],
            Outcome = (_, _, _) => WorkerTestState.Passed,
            HoldAfterStart = (_, _) =>
            {
                bothStarted.Signal();
                return release.Task;
            },
            ProcessIdFor = index => 7000 + index
        };

        var supervisor = new Supervisor(factory)
        {
            Time = time,
            MaxParallelWorkers = 2,
            PublishToMonitor = true,
            MonitorSink = sink
        };

        var run = supervisor.Run();
        bothStarted.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();

        // The second start landed inside the first's interval and is waiting on the trailing post.
        time.Advance(SupervisorRunPublisher.ProgressInterval);

        var latest = sink.Progress[^1];
        latest.InFlight.ShouldBe(2);
        latest.WorkersBusy.ShouldBe(2);
        latest.Completed.ShouldBe(0);

        release.TrySetResult();
        (await run.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode.ShouldBe(0);
    }
}
