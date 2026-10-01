using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

public class DecisionTableTests
{
    [Fact]
    public async Task return_value_decision_table_all_pass()
    {
        var results = await Specs.Run(Decision_Table_Feature.Define(), "Return-value columns all pass");
        var step = results.Step("the line totals are calculated");

        step.StepStatus.ShouldBe(ResultStatus.success);
        step.IsSetVerification.ShouldBeTrue();
        step.SetVerificationColumns.ShouldBe(new[] { "quantity", "price", "LineTotal" });

        // Input cells are plain (ok); expected column cells are success.
        var row0 = step.Cells.Where(c => c.RowIndex == 0).ToList();
        row0.Single(c => c.Name == "quantity").Status.ShouldBe(ResultStatus.ok);
        row0.Single(c => c.Name == "LineTotal").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task return_value_decision_table_reports_failing_cell()
    {
        var results = await Specs.Run(Decision_Table_Feature.Define(), "Return-value column has a discrepancy");
        var step = results.Step("the line totals are calculated");

        step.StepStatus.ShouldBe(ResultStatus.failed);
        var bad = step.Cells.Single(c => c.RowIndex == 1 && c.Name == "LineTotal");
        bad.Status.ShouldBe(ResultStatus.failed);
        bad.Expected.ShouldBe("9.00");
        bad.Actual.ShouldBe("8.00");
    }

    [Fact]
    public async Task a_throwing_row_is_a_failed_row_and_the_rest_of_the_table_still_runs()
    {
        var results = await Specs.Run(Decision_Table_Feature.Define(),
            "A row that throws still leaves the rows before it on the grid");
        var step = results.Step("the line totals are calculated");

        // A row of a table is an independent case, so the exception is that row's verdict rather
        // than the step's: an assertion-level failure the scenario carries on from. Before this,
        // one bad row aborted the scenario AND discarded every cell, so the reader got an
        // exception with no grid and no way to see which row caused it.
        step.StepStatus.ShouldBe(ResultStatus.failed);
        step.FailureLevel.ShouldBe(FailureLevel.Assertion);

        var rowError = step.Cells.Single(c => c.Name == DecisionTableComparer.RowErrorCell);
        rowError.RowIndex.ShouldBe(1);
        rowError.Status.ShouldBe(ResultStatus.error);
        rowError.DisplayText.ShouldBe("InvalidOperationException: a line cannot have a negative quantity");

        // The rows either side of it were judged on their own merits.
        step.Cells.Single(c => c.RowIndex == 0 && c.Name == "LineTotal").Status
            .ShouldBe(ResultStatus.success);
        step.Cells.Single(c => c.RowIndex == 2 && c.Name == "LineTotal").Status
            .ShouldBe(ResultStatus.success);

        // Nothing is invented for the column the row never produced.
        step.Cells.ShouldNotContain(c => c.RowIndex == 1 && c.Name == "LineTotal");
    }

    [Fact]
    public async Task a_row_that_throws_renders_as_its_own_row_with_the_reason()
    {
        var results = await Specs.Run(Decision_Table_Feature.Define(),
            "A row that throws still leaves the rows before it on the grid");
        var render = SetVerificationRender.FromStepResult(results.Step("the line totals are calculated"));

        render.Rows.Select(r => r.RowType).ShouldBe(new[]
        {
            SetVerificationRowType.Matched,
            SetVerificationRowType.Errored,
            SetVerificationRowType.Matched
        });

        var errored = render.Rows[1];
        errored.AllCellsOk.ShouldBeFalse();
        errored.Description.ShouldContain("a line cannot have a negative quantity");

        // The inputs it was given are still shown; the column it never produced is not empty-green.
        errored.Cells.Single(c => c.Column == "quantity").DisplayText.ShouldBe("-1");
        errored.Cells.Single(c => c.Column == "LineTotal").Status.ShouldBe(ResultStatus.error);
    }

    [Fact]
    public async Task out_param_decision_table_all_pass()
    {
        var results = await Specs.Run(Decision_Table_Feature.Define(), "Out-param columns all pass");
        var step = results.Step("the divmod results are");

        step.StepStatus.ShouldBe(ResultStatus.success);
        step.Cells.Where(c => c.Name == "quotient").All(c => c.Status == ResultStatus.success).ShouldBeTrue();
    }

    [Fact]
    public async Task out_param_decision_table_reports_failing_cell()
    {
        var results = await Specs.Run(Decision_Table_Feature.Define(), "Out-param column has a discrepancy");
        var step = results.Step("the divmod results are");

        step.StepStatus.ShouldBe(ResultStatus.failed);
        var bad = step.Cells.Single(c => c.RowIndex == 0 && c.Name == "remainder");
        bad.Status.ShouldBe(ResultStatus.failed);
        bad.Expected.ShouldBe("9");
        bad.Actual.ShouldBe("2");
    }
}
