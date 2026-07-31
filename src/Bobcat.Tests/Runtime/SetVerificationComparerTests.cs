using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

public class SetVerificationComparerTests
{
    public record Item(string Sku, string Name, int Quantity);

    private static Dictionary<string, string> row(string sku, string name, string qty)
        => new() { ["Sku"] = sku, ["Name"] = name, ["Quantity"] = qty };

    [Fact]
    public void matching_rows_produce_structured_success_cells()
    {
        var actual = new[] { new Item("SKU-1", "Widget", 90) };
        var expected = new[] { row("SKU-1", "Widget", "90") };
        var result = new StepResult("step", 0);

        SetVerificationComparer.Compare(actual, expected, new[] { "Sku" }, result);

        result.StepStatus.ShouldBe(ResultStatus.success);
        var qtyCell = result.Cells.Single(c => c.Name == "Quantity");
        qtyCell.Status.ShouldBe(ResultStatus.success);
        qtyCell.Expected.ShouldBe("90");
        qtyCell.Actual.ShouldBe("90");
    }

    [Fact]
    public void mismatched_cell_is_typed_and_failed()
    {
        var actual = new[] { new Item("SKU-1", "Widget", 90) };
        var expected = new[] { row("SKU-1", "Widget", "85") };
        var result = new StepResult("step", 0);

        SetVerificationComparer.Compare(actual, expected, new[] { "Sku" }, result);

        result.StepStatus.ShouldBe(ResultStatus.failed);
        var qtyCell = result.Cells.Single(c => c.Name == "Quantity");
        qtyCell.Status.ShouldBe(ResultStatus.failed);
        qtyCell.Expected.ShouldBe("85");
        qtyCell.Actual.ShouldBe("90");
        qtyCell.DisplayText.ShouldBe("expected '85', got '90'");
    }

    [Fact]
    public void missing_and_extra_rows_are_reported()
    {
        var actual = new[] { new Item("SKU-2", "Gadget", 10) };
        var expected = new[] { row("SKU-1", "Widget", "90") };
        var result = new StepResult("step", 0);

        SetVerificationComparer.Compare(actual, expected, new[] { "Sku" }, result);

        result.StepStatus.ShouldBe(ResultStatus.failed);
        result.Cells.ShouldContain(c => c.Name == "missing-row");
        result.Cells.ShouldContain(c => c.Name == "extra-row");
    }
}
