using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// <c>VerifySet</c> driven from a <c>.feature</c> file. The projected half of the same grammar is
/// <see cref="ProjectedVerifySetTests"/> — same fixture, same methods, table literals instead of a
/// document.
/// </summary>
public class VerifySetTests
{
    private static async Task<(ExecutionResults Results, VerifySetFixture Fixture)> run(string scenario)
    {
        var fixture = new VerifySetFixture();
        var results = await Specs.Run(Verify_Set_Feature.Define(), scenario, fixture);
        return (results, fixture);
    }

    [Fact]
    public async Task an_unordered_set_the_step_verified_itself_renders_one_grid()
    {
        var (results, _) = await run("A set the step verifies itself");
        var step = results.Step("the inventory should be");

        step.StepStatus.ShouldBe(ResultStatus.success);

        // The same shape a [SetVerification] step produces, because it is the same comparison.
        step.IsSetVerification.ShouldBeTrue();
        step.SetVerificationColumns.ShouldBe(new[] { "Sku", "ProductName", "Quantity" });
        SetVerificationRender.FromStepResult(step).Rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task a_key_column_turns_a_wrong_value_into_one_failed_cell()
    {
        var (results, _) = await run("A wrong value is one cell, because the key column identified the row");
        var step = results.Step("the inventory should be");

        step.StepStatus.ShouldBe(ResultStatus.failed);

        var quantity = step.Cells.Single(c => c.Name == "Quantity");
        quantity.Status.ShouldBe(ResultStatus.failed);
        quantity.Expected.ShouldBe("85");
        quantity.Actual.ShouldBe("90");

        // The row was still matched, so neither marker cell appears.
        step.Cells.ShouldNotContain(c => c.Name == "missing-row" || c.Name == "extra-row");
    }

    [Fact]
    public async Task a_missing_row_and_an_extra_row_both_fail_the_step()
    {
        var (results, _) = await run("A missing row and an extra row");
        var step = results.Step("the inventory should be");

        step.StepStatus.ShouldBe(ResultStatus.failed);

        var render = SetVerificationRender.FromStepResult(step);
        render.Rows.Select(r => r.RowType)
            .ShouldBe(new[] { SetVerificationRowType.Missing, SetVerificationRowType.Extra });

        // Each one shows its own values in place, so the grid says WHICH row rather than printing
        // a row of dashes and a description to re-read.
        render.Rows[0].Cells.Select(c => c.DisplayText).ShouldBe(new[] { "SKU-001", "Widget", "90" });
        render.Rows[1].Cells.Select(c => c.DisplayText).ShouldBe(new[] { "SKU-002", "Gadget", "12" });
    }

    [Fact]
    public async Task ordered_is_checked_after_matching()
    {
        var (results, _) = await run("An ordered set reports the row that turned up early");
        var step = results.Step("the inventory in order should be");

        step.StepStatus.ShouldBe(ResultStatus.failed);

        // One reordering, not two rows of wrong values.
        step.Cells.ShouldNotContain(c => c.Name == "missing-row" || c.Name == "extra-row");
        var outOfOrder = step.Cells.Single(c => c.Name == SetVerificationComparer.OutOfOrderCell);
        outOfOrder.RowIndex.ShouldBe(1);
        outOfOrder.DisplayText.ShouldContain("found at position 1");
    }

    [Fact]
    public async Task a_set_of_plain_values_infers_its_column_from_a_one_column_table()
    {
        var (results, _) = await run("A set of plain values under the column the table names");
        var step = results.Step("the tags should be");

        step.StepStatus.ShouldBe(ResultStatus.success);
        step.SetVerificationColumns.ShouldBe(new[] { "tag" });

        // Reading properties off a string instead would compare it against Length and Chars, which
        // is what once made every row read as missing and extra at once.
        step.Cells.Where(c => c.Name == "tag").Select(c => c.Actual)
            .ShouldBe(new[] { "urgent", "shipped" });
    }

    [Fact]
    public async Task a_header_with_no_rows_under_it_still_names_the_columns()
    {
        var (results, _) = await run("A table with a header and no rows says the set is empty");
        var step = results.Step("the inventory should be");

        step.StepStatus.ShouldBe(ResultStatus.failed);

        // The header row says what the columns are even with nothing under it, so the one extra row
        // renders in place. Read off the first expected row — as the generated path does — there is
        // no first row and the grid would have had no columns at all.
        step.SetVerificationColumns.ShouldBe(new[] { "Sku", "ProductName", "Quantity" });

        var extra = SetVerificationRender.FromStepResult(step).Rows.Single();
        extra.RowType.ShouldBe(SetVerificationRowType.Extra);
        extra.Cells.Select(c => c.DisplayText).ShouldBe(new[] { "SKU-001", "Widget", "90" });
    }
}
