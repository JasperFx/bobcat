using Bobcat.Engine;
using Bobcat.Monitoring;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Monitoring;

/// <summary>
/// Issue #99 — the progress model for a scenario in flight, as the publisher puts it on the
/// wire: step n of N with elapsed, row k of M for a table grammar, and the [WaitFor] poll
/// loop's interim message.
/// </summary>
public class StepProgressPublishingTests
{
    private sealed class RecordingSink : IMonitorEventSink
    {
        private readonly List<MonitorEvent> _events = new();

        public void Post(MonitorEvent @event)
        {
            lock (_events) _events.Add(@event);
        }

        public IReadOnlyList<MonitorEvent> Events
        {
            get { lock (_events) return _events.ToArray(); }
        }
    }

    private static readonly MonitorRunInfo info =
        new(Guid.NewGuid(), "TestSuite", "/repo", "main", "in-process");

    private static MonitorPublishingObserver unthrottled(RecordingSink sink)
        => new(sink, info, progressInterval: TimeSpan.Zero);

    private static StepResult finished(string stepId, long start, long end)
    {
        var result = new StepResult(stepId, start, StepKind.Given);
        result.MarkSuccess();
        result.MarkEnded(end);
        return result;
    }

    [Fact]
    public void scenario_started_carries_the_step_count_and_each_step_its_position()
    {
        var sink = new RecordingSink();
        var observer = unthrottled(sink);

        // The executor always calls the four-argument form, passing its own wall-clock stamp —
        // the same value that lands on StepResult.Start (#141), so wire and report agree.
        observer.ScenarioStarted("Orders", "ships", totalSteps: 3);
        observer.StepStarted("s1", StepKind.Given, "an order", scenarioElapsedMs: 0);
        observer.StepFinished(finished("s1", 0, 5));
        observer.StepStarted("s2", StepKind.When, "it ships", scenarioElapsedMs: 5);

        sink.Events.OfType<ScenarioStarted>().Single().TotalSteps.ShouldBe(3);

        var starts = sink.Events.OfType<StepStarted>().ToArray();
        starts.Select(s => s.StepNumber).ShouldBe([1, 2]);
        starts.ShouldAllBe(s => s.TotalSteps == 3);
        starts.Select(s => s.ScenarioElapsedMs).ShouldBe([0L, 5L]);

        // StepFinished rides the step's own end stamp, not a second reading.
        sink.Events.OfType<StepFinished>().Single().ScenarioElapsedMs.ShouldBe(5);
    }

    [Fact]
    public void the_two_argument_scenario_started_still_publishes_without_a_count()
    {
        // A harness that only knows the older observer shape must not lose the event.
        var sink = new RecordingSink();
        var observer = unthrottled(sink);

        observer.ScenarioStarted("Orders", "ships");
        observer.StepStarted("s1", StepKind.Given, "an order");

        sink.Events.OfType<ScenarioStarted>().Single().TotalSteps.ShouldBeNull();
        var start = sink.Events.OfType<StepStarted>().Single();
        start.StepNumber.ShouldBe(1);
        start.TotalSteps.ShouldBeNull();
        // The three-argument caller has no scenario clock to offer; null is honest, not zero.
        start.ScenarioElapsedMs.ShouldBeNull();
    }

    [Fact]
    public void step_numbering_restarts_with_each_attempt()
    {
        var sink = new RecordingSink();
        var observer = unthrottled(sink);

        observer.ScenarioStarted("Orders", "ships", 2);
        observer.StepStarted("s1", StepKind.Given, "an order");
        observer.StepStarted("s2", StepKind.Then, "it fails");
        observer.ScenarioStarted("Orders", "ships", 2);
        observer.StepStarted("s1", StepKind.Given, "an order");

        sink.Events.OfType<StepStarted>().Select(s => s.StepNumber).ShouldBe([1, 2, 1]);
        sink.Events.OfType<ScenarioStarted>().Select(s => s.Attempt).ShouldBe([1, 2]);
    }

    [Fact]
    public void row_progress_reaches_the_wire_with_the_step_id_and_no_message()
    {
        var sink = new RecordingSink();
        var observer = unthrottled(sink);

        observer.ScenarioStarted("Customers", "bulk", 1);
        observer.StepStarted("grammar", StepKind.Given, "the following customers exist");
        observer.StepProgress("grammar", StepUpdate.ForRow(1, 3));
        observer.StepProgress("grammar", StepUpdate.ForRow(2, 3));
        observer.StepProgress("grammar", StepUpdate.ForRow(3, 3));

        var progress = sink.Events.OfType<StepProgress>().ToArray();
        progress.Select(p => p.Row).ShouldBe([1, 2, 3]);
        progress.ShouldAllBe(p => p.TotalRows == 3);
        progress.ShouldAllBe(p => p.StepId == "grammar" && p.Uid == "Customers/bulk");
        progress.ShouldAllBe(p => p.Message == null);
        progress.ShouldAllBe(p => p.ElapsedMs >= 0);
    }

    [Fact]
    public void wait_for_progress_reaches_the_wire_as_a_message()
    {
        var sink = new RecordingSink();
        var observer = unthrottled(sink);

        observer.ScenarioStarted("Queue", "drains", 1);
        observer.StepStarted("wait", StepKind.Then, "the queue eventually drains");
        observer.StepProgress("wait", new StepUpdate("waiting… (attempt 3, 250ms); last value 7"));

        var progress = sink.Events.OfType<StepProgress>().Single();
        progress.Message.ShouldBe("waiting… (attempt 3, 250ms); last value 7");
        progress.Row.ShouldBeNull();
        progress.TotalRows.ShouldBeNull();
    }

    [Fact]
    public void a_fast_grammar_is_coalesced_but_the_first_and_last_rows_always_post()
    {
        // 200 rows in a few milliseconds would otherwise be 200 events into a channel that
        // drops on backpressure, crowding out the StepFinished that matters more.
        var sink = new RecordingSink();
        var observer = new MonitorPublishingObserver(sink, info, progressInterval: TimeSpan.FromSeconds(10));

        observer.ScenarioStarted("Customers", "bulk", 1);
        observer.StepStarted("grammar", StepKind.Given, "the following customers exist");
        for (var row = 1; row <= 200; row++)
        {
            observer.StepProgress("grammar", StepUpdate.ForRow(row, 200));
        }

        var progress = sink.Events.OfType<StepProgress>().ToArray();
        progress.Select(p => p.Row).ShouldBe([1, 200]);
    }

    [Fact]
    public void the_coalescing_window_resets_for_each_step()
    {
        var sink = new RecordingSink();
        var observer = new MonitorPublishingObserver(sink, info, progressInterval: TimeSpan.FromSeconds(10));

        observer.ScenarioStarted("Customers", "bulk", 2);
        observer.StepStarted("g1", StepKind.Given, "first table");
        observer.StepProgress("g1", StepUpdate.ForRow(1, 5));
        observer.StepProgress("g1", StepUpdate.ForRow(2, 5));
        observer.StepStarted("g2", StepKind.Given, "second table");
        observer.StepProgress("g2", StepUpdate.ForRow(1, 5));

        // g1's first row, then g2's first row — the window does not carry over.
        sink.Events.OfType<StepProgress>().Select(p => p.StepId).ShouldBe(["g1", "g2"]);
    }

    [Fact]
    public async Task the_runner_announces_the_step_count_before_the_first_step()
    {
        var sink = new RecordingSink();
        var runner = new BobcatRunner { SuppressConsoleOutput = true };

        var scenario = new ScenarioDefinition("three steps", [], (_, plan) =>
        {
            plan.Add(new DelegateExecutionStep("s1", StepKind.Given, "one", (_, _, _) => Task.CompletedTask));
            plan.Add(new DelegateExecutionStep("s2", StepKind.When, "two", (_, _, _) => Task.CompletedTask));
            plan.Add(new DelegateExecutionStep("s3", StepKind.Then, "three", (_, _, _) => Task.CompletedTask));
        });
        runner.AddFeature(new FeatureDefinition("Counted", typeof(CountedFixture), [scenario]));
        runner.AddObserver(unthrottled(sink));

        (await runner.RunAll()).ExitCode.ShouldBe(0);

        sink.Events.OfType<ScenarioStarted>().Single().TotalSteps.ShouldBe(3);
        sink.Events.OfType<StepStarted>().Select(s => (s.StepNumber, s.TotalSteps))
            .ShouldBe([(1, 3), (2, 3), (3, 3)]);
    }

    public class CountedFixture : Fixture;
}
