using Bobcat.Monitoring;

namespace Bobcat.Projected.Acceptance.Tests;

/// <summary>
/// What a watcher sees of a table step while and after it runs, from the projected lane. The
/// Gherkin lane's <c>[TableGrammar]</c> row ticks ("row 1 of 2") die with #468; the projected form of
/// "a long table shows progress" is the cells-so-far update (#387), and the authority is the cells
/// <c>StepFinished</c> carries.
/// </summary>
public class TableProgressTests
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

    private const string table = """
        | quantity | price | LineTotal |
        | 2        | 10.00 | 20.00     |
        | 4        | 2.00  | 9.00      |
        """;

    private static async Task<IReadOnlyList<MonitorEvent>> run()
    {
        var sink = new RecordingSink();
        var grammar = new DecisionTableTests.LineGrammar();

        using (ScenarioRecorder.Begin("Projected Acceptance", "table progress", sink, Guid.NewGuid()))
        {
            await grammar.TheLineTotalsAreCalculated(table);
        }

        return sink.Events;
    }

    [Fact]
    public async Task a_table_step_shows_its_cells_before_it_finishes()
    {
        var events = await run();

        var finished = events.OfType<StepFinished>().Single();
        var progress = events.OfType<StepProgress>().Where(p => p.StepId == finished.StepId).ToList();

        progress.ShouldNotBeEmpty();
        progress.First().Cells.ShouldNotBeNull().ShouldNotBeEmpty();
        events.ToList().IndexOf(progress.First()).ShouldBeLessThan(events.ToList().IndexOf(finished));
    }

    [Fact]
    public async Task the_finished_step_carries_the_judged_cells_and_the_column_order()
    {
        var finished = (await run()).OfType<StepFinished>().Single();

        finished.Status.ShouldBe("Failed");
        finished.Columns.ShouldBe(["quantity", "price", "LineTotal"]);

        var wrong = finished.Cells.ShouldNotBeNull().Single(c => c.Status == "failed");
        wrong.Name.ShouldBe("LineTotal");
        wrong.RowIndex.ShouldBe(1);
        wrong.Expected.ShouldBe("9.00");
        wrong.Actual.ShouldBe("8.00");
    }

    /// <summary>
    /// <c>TableRun.Report</c> replaces the cells the interceptor echoed from the table rather than
    /// adding beside them (bobcat#471), so the wire carries each cell once: 6 for a 2x3 table, where it
    /// used to carry 12.
    /// </summary>
    [Fact]
    public async Task the_finished_step_carries_one_cell_per_row_and_column()
    {
        var finished = (await run()).OfType<StepFinished>().Single();

        // 2 rows x 3 columns, the expected column judged and the inputs plain.
        var cells = finished.Cells.ShouldNotBeNull();
        cells.Count.ShouldBe(6);
        cells.GroupBy(c => (c.RowIndex, c.Name)).ShouldAllBe(g => g.Count() == 1);
    }

    [Fact]
    public async Task the_table_shows_before_the_step_reports_its_grid()
    {
        // The echo is what a watcher sees while the step is still working on it (issue #387); the
        // replacement only happens when the grid arrives.
        var events = await run();
        var finished = events.OfType<StepFinished>().Single();

        var first = events.OfType<StepProgress>().First(p => p.StepId == finished.StepId);
        first.Cells.ShouldNotBeNull().Count.ShouldBe(6);
        first.Cells.ShouldAllBe(c => c.Status == "ok");
    }
}
