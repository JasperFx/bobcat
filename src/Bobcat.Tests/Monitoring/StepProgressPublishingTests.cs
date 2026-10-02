using Bobcat.Engine;
using Bobcat.Engine.Verification;
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
        new(Guid.NewGuid(), "TestResources", "/repo", "main", "in-process");

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

    // --- issue #387: the cells a step has made so far ---

    [Fact]
    public void interim_progress_carries_the_cells_the_step_has_made_so_far()
    {
        var sink = new RecordingSink();
        var observer = unthrottled(sink);

        observer.StepStarted("s1", StepKind.Then, "the totals are", scenarioElapsedMs: 0);

        // A [WaitFor] poll loop reporting what it last saw — the shape that already filled
        // StepUpdate.Cells and had it dropped at the wire.
        observer.StepProgress("s1", new StepUpdate("still waiting")
        {
            Cells = [CellCheck.ForValue("total", 4, "5")]
        });

        var progress = sink.Events.OfType<StepProgress>().Single();
        var cell = progress.Cells.ShouldHaveSingleItem();
        cell.Name.ShouldBe("total");
        cell.Expected.ShouldBe("5");
        cell.Actual.ShouldBe("4");
        cell.Status.ShouldBe(nameof(ResultStatus.failed));
    }

    [Fact]
    public void an_update_with_no_cells_says_nothing_about_them_rather_than_that_there_are_none()
    {
        var sink = new RecordingSink();
        var observer = unthrottled(sink);

        observer.StepStarted("s1", StepKind.Given, "the rows are", scenarioElapsedMs: 0);
        observer.StepProgress("s1", StepUpdate.ForRow(1, 3));

        // Null, not empty. A receiver keeps the last set it had, so a row tick interleaved with a
        // cell-bearing update cannot blank the cells that update showed.
        sink.Events.OfType<StepProgress>().Single().Cells.ShouldBeNull();
    }

    [Fact]
    public void the_whole_set_is_restated_every_time_so_a_dropped_update_costs_nothing()
    {
        var sink = new RecordingSink();
        var observer = unthrottled(sink);

        observer.StepStarted("s1", StepKind.Then, "the totals are", scenarioElapsedMs: 0);

        var first = CellCheck.ForValue("a", 1, "1");
        var second = CellCheck.ForValue("b", 2, "2");

        observer.StepProgress("s1", new StepUpdate(null) { Cells = [first] });
        observer.StepProgress("s1", new StepUpdate(null) { Cells = [first, second] });

        var updates = sink.Events.OfType<StepProgress>().ToArray();
        updates[0].Cells!.Select(c => c.Name).ShouldBe(["a"]);
        updates[1].Cells!.Select(c => c.Name).ShouldBe(["a", "b"]);
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

    // --- issue #396: the Gherkin lane's literal cells reach the wire too ---

    [Fact]
    public void a_gherkin_table_steps_input_cells_carry_their_value()
    {
        // The issue asked whether the Gherkin [Table] path had the same gap as the projected
        // lane's table literal. It did: the generated input cells and a [TableGrammar]'s row cells
        // are both built with the plain-value constructor, and the projection sent
        // Expected/Actual/Note and never DisplayText. One shared projection now, so neither lane
        // can lose a value the other keeps.
        var sink = new RecordingSink();
        var observer = unthrottled(sink);

        var result = new StepResult("s1", 0, StepKind.Given) { StepText = "the following rows" };
        result.MarkCells(
            new CellResult("x", ResultStatus.ok, "1") { RowIndex = 0 },
            new CellResult("y", ResultStatus.ok, "2") { RowIndex = 0 });
        result.MarkSuccess();
        result.MarkEnded(5);

        observer.ScenarioStarted("Orders", "ships", totalSteps: 1);
        observer.StepStarted("s1", StepKind.Given, "the following rows", scenarioElapsedMs: 0);
        observer.StepFinished(result);

        var cells = sink.Events.OfType<StepFinished>().ShouldHaveSingleItem().Cells!;

        cells.Select(c => c.Name).ShouldBe(["x", "y"]);
        cells.Select(c => c.Value).ShouldBe(["1", "2"]);
        cells.ShouldAllBe(c => c.Expected == null && c.Actual == null);
    }

    [Fact]
    public void a_noted_cell_carries_its_note_and_no_value()
    {
        // The set comparer's missing-row and extra-row cells say a sentence rather than hold a
        // value, so Note is their field and Value stays empty — a cell says exactly one thing.
        var sink = new RecordingSink();
        var observer = unthrottled(sink);

        var result = new StepResult("s1", 0, StepKind.Then);
        result.MarkCells(new CellResult("missing-row", ResultStatus.missing)
        {
            Note = "Expected row not found: id=7",
            RowIndex = 0
        });
        result.MarkEnded(5);

        observer.ScenarioStarted("Orders", "ships", totalSteps: 1);
        observer.StepStarted("s1", StepKind.Then, "the rows are", scenarioElapsedMs: 0);
        observer.StepFinished(result);

        var cell = sink.Events.OfType<StepFinished>().ShouldHaveSingleItem().Cells!.ShouldHaveSingleItem();

        cell.Note.ShouldBe("Expected row not found: id=7");
        cell.Value.ShouldBeNull();
    }
}
