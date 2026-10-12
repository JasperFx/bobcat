using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;

namespace Bobcat.Projected.Acceptance.Tests;

/// <summary>
/// <c>DecisionTable.feature</c> from the projected lane: a step hands its table to
/// <see cref="TableRunner.Run(object,string,StepTable,IStepContext?)"/>, which calls a row method once
/// per row and, when the method returns a value and exactly one column names no parameter, compares
/// that column against it. The same rule the generator applies to a <c>[DecisionTable]</c> method.
/// </summary>
public class DecisionTableTests
{
    public class LineGrammar
    {
        [Then("the line totals are calculated")]
        internal async Task TheLineTotalsAreCalculated(StepTable table) => await TableRunner.Run(this, nameof(lineTotal), table);

        private decimal lineTotal(int quantity, decimal price)
        {
            if (quantity < 0) throw new InvalidOperationException("a line cannot have a negative quantity");
            return quantity * price;
        }

        [Then("the divmod results are")]
        internal async Task TheDivModResultsAre(StepTable table) => await TableRunner.Run(this, nameof(divMod), table);

        private void divMod(int dividend, int divisor, out int quotient, out int remainder)
        {
            quotient = dividend / divisor;
            remainder = dividend % divisor;
        }

        [Then("the next step still runs")]
        internal bool TheNextStepStillRuns() => true;
    }

    private readonly LineGrammar _grammar = new();

    private Task<(ScenarioRecorder.Recording Recording, SpecRender Render)> lineTotals(string scenario, string table)
        => Projected.RunAsync(scenario, () => _grammar.TheLineTotalsAreCalculated(table));

    private static SetVerificationRender gridOf(SpecRender render, string step)
        => render.Step(step).SetVerification.ShouldNotBeNull();

    [Fact]
    public async Task return_value_columns_all_pass()
    {
        var (recording, render) = await lineTotals("all pass", """
            | quantity | price | LineTotal |
            | 2        | 10.00 | 20.00     |
            | 3        | 5.00  | 15.00     |
            """);

        recording.Step("the line totals are calculated").Status.ShouldBe(ResultStatus.success);

        var grid = gridOf(render, "the line totals are calculated");
        grid.Columns.ShouldBe(["quantity", "price", "LineTotal"]);
        grid.Rows.ShouldAllBe(r => r.AllCellsOk);

        // Input cells are plain; the expected column is judged.
        grid.Rows[0].Cells.Single(c => c.Column == "quantity").Status.ShouldBe(ResultStatus.ok);
        grid.Rows[0].Cells.Single(c => c.Column == "LineTotal").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task a_return_value_column_reports_its_failing_cell()
    {
        var (recording, render) = await lineTotals("discrepancy", """
            | quantity | price | LineTotal |
            | 2        | 10.00 | 20.00     |
            | 4        | 2.00  | 9.00      |
            """);

        recording.Step("the line totals are calculated").Status.ShouldBe(ResultStatus.failed);

        var bad = gridOf(render, "the line totals are calculated").Rows[1].Cells.Single(c => c.Column == "LineTotal");
        bad.Status.ShouldBe(ResultStatus.failed);
        bad.Expected.ShouldBe("9.00");
        bad.Actual.ShouldBe("8.00");
    }

    private const string throwingRow = """
        | quantity | price | LineTotal |
        | 2        | 10.00 | 20.00     |
        | -1       | 5.00  | 0.00      |
        | 3        | 5.00  | 15.00     |
        """;

    [Fact]
    public async Task a_throwing_row_is_a_row_error_and_the_rest_of_the_table_still_runs()
    {
        var (recording, render) = await Projected.RunAsync("throwing row", async () =>
        {
            await _grammar.TheLineTotalsAreCalculated(throwingRow);

            // The row's exception is that row's verdict, not the step's throw.
            _grammar.TheNextStepStillRuns();
        });

        var step = recording.Step("the line totals are calculated");
        var rowError = step.Cells.Single(c => c.Name == DecisionTableComparer.RowErrorCell);
        rowError.RowIndex.ShouldBe(1);
        rowError.Status.ShouldBe(ResultStatus.error);
        rowError.DisplayText.ShouldBe("InvalidOperationException: a line cannot have a negative quantity");

        recording.Step("the next step still runs").Status.ShouldBe(ResultStatus.success);

        // The rows either side were judged on their own merits, and nothing is invented for the
        // column the throwing row never produced.
        var grid = gridOf(render, "the line totals are calculated");
        grid.Rows[0].Cells.Single(c => c.Column == "LineTotal").Status.ShouldBe(ResultStatus.success);
        grid.Rows[2].Cells.Single(c => c.Column == "LineTotal").Status.ShouldBe(ResultStatus.success);
        step.Cells.ShouldNotContain(c => c.RowIndex == 1 && c.Name == "LineTotal" && c.Status == ResultStatus.success);
    }

    [Fact]
    public async Task a_throwing_row_renders_as_its_own_row_with_the_reason()
    {
        var (_, render) = await lineTotals("throwing row renders", throwingRow);
        var grid = gridOf(render, "the line totals are calculated");

        grid.Rows.Select(r => r.RowType).ShouldBe(
        [
            SetVerificationRowType.Matched,
            SetVerificationRowType.Errored,
            SetVerificationRowType.Matched
        ]);

        var errored = grid.Rows[1];
        errored.AllCellsOk.ShouldBeFalse();
        errored.Description.ShouldNotBeNull().ShouldContain("a line cannot have a negative quantity");

        // The inputs it was given are still shown, and the column it never produced is not green.
        errored.Cells.Single(c => c.Column == "quantity").DisplayText.ShouldBe("-1");
        errored.Cells.Single(c => c.Column == "LineTotal").Status.ShouldNotBe(ResultStatus.success);
    }

    /// <summary>
    /// The interceptor echoes a table step's cells as plain <c>ok</c> inputs, and the grid
    /// <c>TableRun.Report</c> reports replaces them (bobcat#471). Before, the echo stayed beside the
    /// grid and the document's own text stood in, as <c>ok</c>, for the column a throwing row never
    /// produced.
    /// </summary>
    [Fact]
    public async Task the_column_a_throwing_row_never_produced_renders_as_an_error()
    {
        var (recording, render) = await lineTotals("throwing row column", throwingRow);

        gridOf(render, "the line totals are calculated").Rows[1].Cells.Single(c => c.Column == "LineTotal").Status
            .ShouldBe(ResultStatus.error);

        // Nothing was invented for it either: the row has its error and its two inputs, no LineTotal.
        recording.Step("the line totals are calculated").Cells.Where(c => c.RowIndex == 1).Select(c => c.Name)
            .ShouldBe([DecisionTableComparer.RowErrorCell, "quantity", "price"], ignoreOrder: true);
    }

    /// <summary>
    /// A row whose code threw is an exception, not a wrong, so the step is <c>error</c> (bobcat#471).
    /// A wrong value, a missing row or an extra row is <c>failed</c>
    /// (<see cref="VerifySetTests.an_extra_row_alone_fails_the_step"/>). The Gherkin lane marked both
    /// <c>failed</c>; the projected lane keeps the distinction.
    /// </summary>
    [Fact]
    public async Task a_throwing_row_errors_the_step()
    {
        var (recording, _) = await lineTotals("throwing row verdict", throwingRow);

        recording.Step("the line totals are calculated").Status.ShouldBe(ResultStatus.error);
    }

    [Fact(Skip = "Projected-lane gap (bobcat#471 phase 2): TableRunner.Run cannot bind an `out` parameter, " +
                 "so a row method shaped like a Gherkin [DecisionTable] with out-param outputs reports every " +
                 "row as a row-error. The projected idiom is SpecAssert.Check(..., rowIndex: i) per output; " +
                 "needs a maintainer decision on whether TableRunner should support out-param outputs at all.")]
    public async Task out_param_columns_are_compared_per_row()
    {
        var (recording, render) = await Projected.RunAsync("out params", () => _grammar.TheDivModResultsAre("""
            | dividend | divisor | quotient | remainder |
            | 17       | 5       | 3        | 9         |
            | 10       | 3       | 3        | 1         |
            """));

        recording.Step("the divmod results are").Status.ShouldBe(ResultStatus.failed);

        var grid = gridOf(render, "the divmod results are");
        grid.Rows[1].AllCellsOk.ShouldBeTrue();

        var bad = grid.Rows[0].Cells.Single(c => c.Column == "remainder");
        bad.Status.ShouldBe(ResultStatus.failed);
        bad.Expected.ShouldBe("9");
        bad.Actual.ShouldBe("2");
    }
}
