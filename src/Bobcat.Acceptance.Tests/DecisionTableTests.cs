using Bobcat.Engine;
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
    public async Task a_throwing_row_keeps_the_grid_up_to_where_it_got()
    {
        var results = await Specs.Run(Decision_Table_Feature.Define(),
            "A row that throws still leaves the rows before it on the grid");
        var step = results.Step("the line totals are calculated");

        // The exception is still the step's verdict — that tier did not change. What changed is
        // that the table survives it: before, one throwing row discarded every cell and the
        // reader got an exception with no grid and no way to see which row caused it.
        step.StepStatus.ShouldBe(ResultStatus.error);
        step.IsSetVerification.ShouldBeTrue();
        step.Cells.Where(c => c.RowIndex == 0).ShouldNotBeEmpty();
        step.Cells.Single(c => c.RowIndex == 0 && c.Name == "LineTotal").Status
            .ShouldBe(ResultStatus.success);

        // ...and nothing is invented for the row that threw, or the ones after it.
        step.Cells.ShouldAllBe(c => c.RowIndex == 0);
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
