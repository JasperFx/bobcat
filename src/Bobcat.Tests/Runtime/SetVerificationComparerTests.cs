using Bobcat.Engine;
using Bobcat.Rendering;
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

    [Fact]
    public void an_extra_row_alone_fails_the_step()
    {
        var actual = new[] { new Item("SKU-1", "Widget", 90), new Item("SKU-2", "Gadget", 10) };
        var expected = new[] { row("SKU-1", "Widget", "90") };
        var result = new StepResult("step", 0);

        SetVerificationComparer.Compare(actual, expected, new[] { "Sku" }, result);

        // Every expected row matched, so nothing else here is wrong — but the set is not the set
        // the specification described.
        result.StepStatus.ShouldBe(ResultStatus.failed);
    }

    [Fact]
    public void a_missing_row_carries_its_expected_values_per_column()
    {
        var actual = new[] { new Item("SKU-2", "Gadget", 10) };
        var expected = new[] { row("SKU-1", "Widget", "90") };
        var result = new StepResult("step", 0);

        SetVerificationComparer.Compare(actual, expected, new[] { "Sku" }, result);

        var missingRow = result.Cells.Single(c => c.Name == "missing-row").RowIndex;
        var cells = result.Cells.Where(c => c.RowIndex == missingRow && c.Name != "missing-row").ToList();

        cells.Select(c => c.Name).ShouldBe(new[] { "Sku", "Name", "Quantity" });
        cells.Select(c => c.Expected).ShouldBe(new[] { "SKU-1", "Widget", "90" });
    }

    [Fact]
    public void an_extra_row_carries_its_actual_values_per_column()
    {
        var actual = new[] { new Item("SKU-2", "Gadget", 10) };
        var expected = new[] { row("SKU-1", "Widget", "90") };
        var result = new StepResult("step", 0);

        SetVerificationComparer.Compare(actual, expected, new[] { "Sku" }, result);

        var extraRow = result.Cells.Single(c => c.Name == "extra-row").RowIndex;
        var cells = result.Cells.Where(c => c.RowIndex == extraRow && c.Name != "extra-row").ToList();

        cells.Select(c => c.Name).ShouldBe(new[] { "Sku", "Name", "Quantity" });
        cells.Select(c => c.Actual).ShouldBe(new[] { "SKU-2", "Gadget", "10" });
    }

    [Fact]
    public void the_values_of_an_absent_row_do_not_change_what_the_row_counts_as()
    {
        var actual = new[] { new Item("SKU-2", "Gadget", 10) };
        var expected = new[] { row("SKU-1", "Widget", "90") };
        var result = new StepResult("step", 0);

        SetVerificationComparer.Compare(actual, expected, new[] { "Sku" }, result);

        var counts = new Counts();
        result.Tabulate(counts);

        // one missing row and one extra row, plus the step itself
        counts.Errors.ShouldBe(2);
        counts.Rights.ShouldBe(0);
    }

    [Fact]
    public void the_render_shows_an_absent_rows_values_in_place()
    {
        var actual = new[] { new Item("SKU-2", "Gadget", 10) };
        var expected = new[] { row("SKU-1", "Widget", "90") };
        var result = new StepResult("step", 0);

        SetVerificationComparer.Compare(actual, expected, new[] { "Sku" }, result);

        var render = SetVerificationRender.FromStepResult(result);

        var missing = render.Rows.Single(r => r.RowType == SetVerificationRowType.Missing);
        missing.Cells.Select(c => c.DisplayText).ShouldBe(new[] { "SKU-1", "Widget", "90" });
        missing.Cells.ShouldAllBe(c => c.Status == ResultStatus.missing);
        missing.Description.ShouldBe("Expected row not found: Sku=SKU-1, Name=Widget, Quantity=90");

        var extra = render.Rows.Single(r => r.RowType == SetVerificationRowType.Extra);
        extra.Cells.Select(c => c.DisplayText).ShouldBe(new[] { "SKU-2", "Gadget", "10" });
        extra.Cells.ShouldAllBe(c => c.Status == ResultStatus.invalid);
    }

    [Fact]
    public void a_producer_that_carried_only_a_description_still_renders_a_row()
    {
        var result = new StepResult("step", 0)
        {
            IsSetVerification = true,
            SetVerificationColumns = new[] { "Sku", "Name" }
        };
        result.MarkCells(new CellResult("missing-row", ResultStatus.missing, "Expected row not found: Sku=SKU-1"));

        var render = SetVerificationRender.FromStepResult(result);

        var missing = render.Rows.Single();
        missing.RowType.ShouldBe(SetVerificationRowType.Missing);
        missing.Cells.ShouldBeEmpty();
        missing.Description.ShouldBe("Expected row not found: Sku=SKU-1");
    }
}
