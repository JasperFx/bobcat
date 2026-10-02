using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Rendering;

/// <summary>
/// The Status column of a set verification grid, which is the whole reason a set verification is
/// worth reading as a grid: the four outcomes have to be told apart at a glance.
/// </summary>
/// <remarks>
/// Asserted on the render model rather than on Spectre's output, because the row TYPE is what the
/// renderer switches a word and a colour on, and that is the fact a viewer on the wire reads too.
/// The colour mapping itself lives in <c>CommandLineRenderer.RenderSetVerification</c>:
/// OK green, ORDER yellow, MISSING red, EXTRA red.
/// </remarks>
public class SetVerificationStatusTests
{
    public record Row(string Name, int Amount);

    private static SetVerificationRender compare(Row[] actual, (string Name, string Amount)[] expected,
        bool ordered)
    {
        var rows = expected
            .Select(e => new Dictionary<string, string> { ["Name"] = e.Name, ["Amount"] = e.Amount })
            .ToList();

        var run = SetVerificationComparer.Cells(actual, rows, ["Name"], ordered);
        return SetVerificationRender.FromCells(run.Columns, run.Cells);
    }

    [Fact]
    public void a_row_in_the_position_the_document_wrote_it_is_matched()
    {
        var grid = compare(
            [new Row("Cord", 100), new Row("Drill", 200)],
            [("Cord", "100"), ("Drill", "200")],
            ordered: true);

        grid.Rows.Select(r => r.RowType)
            .ShouldBe(new[] { SetVerificationRowType.Matched, SetVerificationRowType.Matched });

        // Every value agreed, which is what makes the row green.
        grid.Rows.ShouldAllBe(r => r.AllCellsOk);
    }

    [Fact]
    public void a_row_that_matches_in_the_wrong_place_is_out_of_order_and_not_a_failure_of_its_values()
    {
        var grid = compare(
            [new Row("Cord", 100), new Row("Drill", 200)],
            [("Drill", "200"), ("Cord", "100")],
            ordered: true);

        // The distinction the Status column exists for: the second row's columns all agree, so the
        // finding is the position, not the data. Its cells stay green and ORDER is yellow, where a
        // wrong value would be red.
        var outOfOrder = grid.Rows.Single(r => r.RowType == SetVerificationRowType.OutOfOrder);
        outOfOrder.Cells.ShouldAllBe(c => c.Status == ResultStatus.success);
        outOfOrder.Description.ShouldContain("found at position");
    }

    [Fact]
    public void a_row_the_document_expects_and_nothing_produced_is_missing()
    {
        var grid = compare(
            [new Row("Cord", 100)],
            [("Cord", "100"), ("Hammer", "300")],
            ordered: true);

        var missing = grid.Rows.Single(r => r.RowType == SetVerificationRowType.Missing);
        missing.Cells.Select(c => c.DisplayText).ShouldBe(new[] { "Hammer", "300" });
    }

    [Fact]
    public void a_row_nothing_asked_for_is_extra()
    {
        var grid = compare(
            [new Row("Cord", 100), new Row("Hammer", 300)],
            [("Cord", "100")],
            ordered: true);

        var extra = grid.Rows.Single(r => r.RowType == SetVerificationRowType.Extra);
        extra.Cells.Select(c => c.DisplayText).ShouldBe(new[] { "Hammer", "300" });
    }

    [Fact]
    public void a_matched_row_with_a_wrong_column_is_a_failure_rather_than_a_position_problem()
    {
        var grid = compare(
            [new Row("Cord", 999)],
            [("Cord", "100")],
            ordered: true);

        // Matched by its key column, so the row is there — one column simply disagrees. That is a
        // different finding from ORDER and from MISSING, and the grid has to say which.
        var row = grid.Rows.ShouldHaveSingleItem();
        row.RowType.ShouldBe(SetVerificationRowType.Matched);
        row.AllCellsOk.ShouldBeFalse();
        row.Cells.Single(c => c.Column == "Amount").DisplayText.ShouldBe("expected '100', got '999'");
    }

    [Fact]
    public void all_four_outcomes_can_appear_in_one_grid_and_stay_distinguishable()
    {
        // The shape the ordered event comparisons will need: one reordering, one absence, one
        // surprise, and a row that was simply right.
        //
        // Drill has to turn up BEHIND Cord to be a reordering. Merely interleaving an unexpected row
        // between them would not be one — that is an extra, which is the whole point of matching
        // before checking order: an insertion stays one extra row instead of reporting every row
        // after it as misplaced.
        var grid = compare(
            [new Row("Drill", 200), new Row("Cord", 100), new Row("Hammer", 300)],
            [("Cord", "100"), ("Drill", "200"), ("Saw", "400")],
            ordered: true);

        grid.Rows.Select(r => r.RowType).ShouldBe(new[]
        {
            SetVerificationRowType.Matched,
            SetVerificationRowType.OutOfOrder,
            SetVerificationRowType.Missing,
            SetVerificationRowType.Extra
        });
    }
}
