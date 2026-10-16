using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// A cell whose text cannot be read as its member's type is an <c>invalid</c> cell in that column, not
/// the row's error (bobcat#544): a defect in the specification, decidable without running anything.
/// </summary>
public class TableRunnerInvalidCellTests
{
    public record Line(string Sku, int Quantity, decimal Price);

    public class Strict
    {
        public Strict(int quantity)
        {
            if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity), "no negative quantities");
            Quantity = quantity;
        }

        public int Quantity { get; }
    }

    private static CellResult cell(TableRun run, int row, string column)
        => run.Cells.Single(c => c.RowIndex == row && c.Name == column);

    [Fact]
    public void a_built_row_whose_cell_will_not_convert_is_invalid_in_that_column()
    {
        var run = TableRunner.Build(typeof(Line), """
            | Sku | Quantity | Price |
            | A1  | 2        | 1.50  |
            | B2  | lots     | 3.00  |
            | C3  | 1        | 9.99  |
            """, out var built);

        run.Succeeded.ShouldBeFalse();
        run.Cells.ShouldNotContain(c => c.Name == DecisionTableComparer.RowErrorCell);

        var quantity = cell(run, 1, "Quantity");
        quantity.Status.ShouldBe(ResultStatus.invalid);
        quantity.DisplayText.ShouldContain("'lots'");
        quantity.DisplayText.ShouldContain("Int32");

        // The row's other inputs are still shown as written, and the bad row is left out.
        cell(run, 1, "Sku").DisplayText.ShouldBe("B2");
        built.OfType<Line>().Select(l => l.Sku).ShouldBe(["A1", "C3"]);
    }

    [Fact]
    public void a_built_row_whose_construction_throws_is_still_a_row_error()
    {
        var run = TableRunner.Build(typeof(Strict), """
            | quantity |
            | -1       |
            """, out _);

        cell(run, 0, DecisionTableComparer.RowErrorCell).Status.ShouldBe(ResultStatus.error);
    }
}
