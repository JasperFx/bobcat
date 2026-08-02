using JasperFx.Testing;
using Bobcat.Monitoring;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issue #84 — what the supervisor uniquely knows, streamed while it happens rather than
/// reported once the run is over.
/// </summary>
public class SupervisorObserverTests
{
    private sealed class RecordingObserver : ISupervisorObserver
    {
        public List<(string Uid, SupervisorAttempt Attempt)> Attempts { get; } = [];
        public List<(string Uid, int NextAttempt, Disposition Disposition)> Retries { get; } = [];
        public List<(int Lane, IReadOnlyList<string> Uids)> LanesStarted { get; } = [];
        public List<int> LanesFinished { get; } = [];
        public List<string> Recycled { get; } = [];
        public List<string> Faults { get; } = [];

        public void AttemptRecorded(string uid, SupervisorAttempt attempt)
        {
            lock (Attempts) Attempts.Add((uid, attempt));
        }

        public void RetryScheduled(string uid, int nextAttempt, Disposition disposition)
            => Retries.Add((uid, nextAttempt, disposition));

        public void LaneStarted(int lane, IReadOnlyList<string> uids)
        {
            lock (LanesStarted) LanesStarted.Add((lane, uids));
        }

        public void LaneFinished(int lane, WorkerRunResult result)
        {
            lock (LanesFinished) LanesFinished.Add(lane);
        }

        public void ResourceRecycled(string name) => Recycled.Add(name);

        public void WorkerFaulted(string fault) => Faults.Add(fault);
    }

    [Fact]
    public async Task every_attempt_is_announced_with_the_policy_verdict_that_followed_it()
    {
        // Passes included: "which tests are running and how are they going" needs them.
        var observer = new RecordingObserver();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("flaky", "Retry=2"), FakeWorkerFactory.Test("clean")],
            Outcome = (uid, attempt, _) =>
                uid == "clean" || attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Failed
        };

        var supervisor = new Supervisor(factory) { RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 } };
        supervisor.AddObserver(observer);

        await supervisor.Run();

        observer.Attempts.Count.ShouldBe(3);

        var flaky = observer.Attempts.Where(a => a.Uid == "flaky").ToList();
        flaky[0].Attempt.AttemptNumber.ShouldBe(1);
        flaky[0].Attempt.Disposition.Kind.ShouldBe(DispositionKind.RetryInProcess);
        flaky[1].Attempt.AttemptNumber.ShouldBe(2);
        flaky[1].Attempt.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task a_scheduled_retry_carries_the_true_attempt_number_and_the_reason()
    {
        // A worker running the retry cannot know it is one, let alone which attempt.
        var observer = new RecordingObserver();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("flaky", "Retry=3")],
            Outcome = (_, attempt, _) => attempt >= 3 ? WorkerTestState.Passed : WorkerTestState.Failed
        };

        var supervisor = new Supervisor(factory) { RetryBudget = new RetryBudget { MaxAttemptsPerTest = 3 } };
        supervisor.AddObserver(observer);

        await supervisor.Run();

        observer.Retries.Select(r => r.NextAttempt).ShouldBe([2, 3]);
        observer.Retries.ShouldAllBe(r => r.Uid == "flaky");
        observer.Retries[0].Disposition.Kind.ShouldBe(DispositionKind.RetryInProcess);
        observer.Retries[0].Disposition.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task a_retry_that_was_asked_for_and_refused_is_never_announced()
    {
        // The budget is the operator's ceiling. Announcing a retry the run then declines to
        // perform would put a lie on the dashboard.
        var observer = new RecordingObserver();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("flaky", "Retry=5")],
            Outcome = (_, _, _) => WorkerTestState.Failed
        };

        var supervisor = new Supervisor(factory) { RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 } };
        supervisor.AddObserver(observer);

        await supervisor.Run();

        // Two attempts ran, so exactly one retry was ever scheduled.
        observer.Retries.Count.ShouldBe(1);
        observer.Attempts.Count.ShouldBe(2);
    }

    [Fact]
    public async Task lanes_recycles_and_worker_deaths_are_all_announced()
    {
        var observer = new RecordingObserver();
        var factory = new FakeWorkerFactory
        {
            Tests =
            [
                FakeWorkerFactory.InClass("Alpha", "one", "RecycleOnRetry=rabbit", "Retry=2"),
                FakeWorkerFactory.InClass("Beta", "two")
            ],
            Outcome = (uid, attempt, _) =>
                uid.StartsWith("Alpha") && attempt == 1 ? WorkerTestState.Failed : WorkerTestState.Passed,
            Fault = w => w.Index == 1 && w.Runs.Count > 0 ? "the worker exited with code 139" : null
        };

        var supervisor = new Supervisor(factory)
        {
            MaxParallelWorkers = 2,
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 }
        };
        supervisor.AddRecyclableResource(new StubRecyclable("rabbit"));
        supervisor.AddObserver(observer);

        await supervisor.Run();

        observer.LanesStarted.Count.ShouldBe(2);
        observer.LanesStarted.Select(l => l.Lane).OrderBy(l => l).ShouldBe([0, 1]);
        observer.LanesFinished.Count.ShouldBe(2);

        observer.Recycled.ShouldBe(["rabbit"]);
        observer.Faults.ShouldContain("the worker exited with code 139");
    }

    [Fact]
    public async Task an_observer_that_throws_is_stepped_over_rather_than_failing_the_run()
    {
        // A dashboard, a log sink or a metrics push must not be able to fail a test run.
        var log = new List<string>();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("a")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        var supervisor = new Supervisor(factory) { Log = log.Add };
        supervisor.AddObserver(new ThrowingObserver());

        var results = await supervisor.Run();

        results.ExitCode.ShouldBe(0);
        log.ShouldContain(line => line.Contains("observer threw and was ignored"));
    }

    [Fact]
    public async Task a_supervised_retry_puts_its_disposition_and_true_attempt_on_the_wire()
    {
        // The defect this feature exists for: a supervised retry re-streamed as attempt 1 from
        // a fresh worker, with neither the disposition nor the reason anywhere on the wire.
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("flaky", "Retry=3")],
            Outcome = (_, attempt, _) => attempt >= 3 ? WorkerTestState.Passed : WorkerTestState.Failed
        };

        var supervisor = new Supervisor(factory)
        {
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 3 },
            PublishToMonitor = true,
            MonitorSink = sink
        };

        await supervisor.Run();

        var retries = sink.Events.OfType<RetryScheduled>().ToList();
        retries.Select(r => r.NextAttempt).ShouldBe([2, 3]);
        retries.ShouldAllBe(r => r.Uid == "flaky");
        retries[0].Disposition.ShouldBe(nameof(DispositionKind.RetryInProcess));
        retries[0].Reason.ShouldNotBeNullOrWhiteSpace();

        // Same run as the bracket, so the dashboard folds them onto the same card.
        var runId = sink.Events.OfType<RunStarted>().Single().RunId;
        retries.ShouldAllBe(r => r.RunId == runId);
    }

    [Fact]
    public async Task nothing_reaches_the_monitor_when_publishing_is_off()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("flaky", "Retry=2")],
            Outcome = (_, attempt, _) => attempt >= 2 ? WorkerTestState.Passed : WorkerTestState.Failed
        };

        var supervisor = new Supervisor(factory)
        {
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 },
            MonitorSink = sink
        };

        await supervisor.Run();

        sink.Events.ShouldBeEmpty();
    }

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

    private sealed class ThrowingObserver : ISupervisorObserver
    {
        public void AttemptRecorded(string uid, SupervisorAttempt attempt)
            => throw new InvalidOperationException("the dashboard is on fire");
    }

    private sealed class StubRecyclable(string name) : Bobcat.Runtime.IRecyclableResource
    {
        public string Name { get; } = name;
        public Task Recycle(CancellationToken token = default) => Task.CompletedTask;
        public Task Start() => Task.CompletedTask;
        public Task ResetBetweenScenarios() => Task.CompletedTask;
        public ValueTask DisposeAsync() => default;
    }
}
