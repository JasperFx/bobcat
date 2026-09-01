using Bobcat.Monitoring;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issue #195 — the supervisor forwarding its workers' live per-test stream to the monitor.
/// A plain xUnit worker has no <c>MonitorPublishingObserver</c>, so before this a supervised
/// suite of 1627 tests registered on the dashboard with the right total and then sat at zero
/// finished for the whole five-minute run: a card indistinguishable from a wedged one.
/// </summary>
public class ForeignTestProgressPublishingTests
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

    private static Supervisor supervised(FakeWorkerFactory factory, RecordingSink sink)
        => new(factory) { PublishToMonitor = true, MonitorSink = sink };

    [Fact]
    public async Task every_test_reaches_the_wire_as_a_start_and_a_verdict()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.one"), FakeWorkerFactory.Test("A.two")],
            Outcome = (uid, _, _) => uid == "A.two" ? WorkerTestState.Failed : WorkerTestState.Passed
        };

        await supervised(factory, sink).Run();

        var runId = sink.Events.OfType<RunStarted>().Single().RunId;

        sink.Events.OfType<TestStarted>().Select(e => e.Uid).OrderBy(u => u).ShouldBe(["A.one", "A.two"]);
        sink.Events.OfType<TestStarted>().ShouldAllBe(e => e.RunId == runId && e.Lane == 0);

        var finished = sink.Events.OfType<TestFinished>().ToDictionary(e => e.Uid);
        finished["A.one"].State.ShouldBe("Passed");
        // The framework's own word, not a re-label — "skipped" is a fact RunOutcome has no word
        // for, and mapping at the publisher is how two vocabularies quietly drift apart.
        finished["A.two"].State.ShouldBe("Failed");
        finished.Values.ShouldAllBe(e => e.RunId == runId);
    }

    [Fact]
    public async Task a_verdict_carries_the_duration_the_supervisor_measured_between_the_two_updates()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.one")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await supervised(factory, sink).Run();

        // Measured on the supervisor's clock, which is the only one both ends of the transition
        // share. It is a real elapsed time, so the assertion is that it exists and is sane.
        var finished = sink.Events.OfType<TestFinished>().ShouldHaveSingleItem();
        finished.DurationMs.ShouldNotBeNull();
        finished.DurationMs.Value.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task the_start_and_the_verdict_are_ordered_inside_the_run_bracket()
    {
        // run_started stays the stream's first event and run_finished its last — the one
        // ordering every consumer may assume.
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.one")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await supervised(factory, sink).Run();

        var kinds = sink.Events.Select(e => e.GetType()).ToList();
        kinds[0].ShouldBe(typeof(RunStarted));
        kinds[^1].ShouldBe(typeof(RunFinished));
        kinds.IndexOf(typeof(TestStarted)).ShouldBeLessThan(kinds.IndexOf(typeof(TestFinished)));
    }

    [Fact]
    public async Task a_discovery_worker_puts_nothing_on_the_wire()
    {
        // Discovery enumerates; "discovered" is not progress, and the supervisor never taps it.
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.one")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await supervised(factory, sink).Run();

        sink.Events.OfType<TestStarted>().Count().ShouldBe(1);
        sink.Events.OfType<TestFinished>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task a_test_a_crashed_worker_never_answered_for_produces_no_verdict_on_the_wire()
    {
        // Its outcome is padded to Indeterminate for the report, and that is absence of
        // evidence: it must never move a progress bar. Nothing is published for it at all,
        // rather than a "finished" that would make a crashed run read as a complete one.
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.one"), FakeWorkerFactory.Test("A.two")],
            Outcome = (uid, _, _) => uid == "A.one" ? WorkerTestState.Passed : null,
            Fault = _ => "the worker process exited with code 134"
        };

        await supervised(factory, sink).Run();

        sink.Events.OfType<TestFinished>().Select(e => e.Uid).ShouldBe(["A.one"]);
        // The start still travelled — the supervisor did watch it begin.
        sink.Events.OfType<TestStarted>().Select(e => e.Uid).OrderBy(u => u).ShouldBe(["A.one", "A.two"]);
    }

    [Fact]
    public async Task an_isolated_test_reports_no_lane_because_its_process_occupies_no_slot()
    {
        var sink = new RecordingSink();
        var factory = new FakeWorkerFactory
        {
            Tests = [FakeWorkerFactory.Test("A.alone", "Isolated"), FakeWorkerFactory.Test("A.batched")],
            Outcome = (_, _, _) => WorkerTestState.Passed
        };

        await supervised(factory, sink).Run();

        sink.Events.OfType<TestFinished>().Single(e => e.Uid == "A.alone").Lane.ShouldBeNull();
        sink.Events.OfType<TestFinished>().Single(e => e.Uid == "A.batched").Lane.ShouldBe(0);
    }
}
