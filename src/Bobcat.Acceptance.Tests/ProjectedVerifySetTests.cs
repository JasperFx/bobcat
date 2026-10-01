using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// The other half of <see cref="VerifySetTests"/>: the same fixture, the same step methods, and a
/// table literal where the feature file had a trailing <c>|...|</c> block.
/// </summary>
/// <remarks>
/// This is the whole point of the <c>StepTable</c> form. A <c>[SetVerification]</c> method takes its
/// expected rows from the generator, so no C# test can call one; here they are an argument, and the
/// document or the call site supplies them.
/// </remarks>
public class ProjectedVerifySetTests
{
    private static SpecRender render(Action<VerifySetFixture> body)
    {
        var fixture = new VerifySetFixture();
        using var recording = ScenarioRecorder.Begin("Verify Set", "a set verified from C#", null, Guid.Empty);
        body(fixture);
        return SpecRender.FromRecording(recording);
    }

    private static void twoItems(VerifySetFixture fixture) => fixture.TheInventoryIs("""
        | Sku     | ProductName | Quantity |
        | SKU-001 | Widget      | 90       |
        | SKU-002 | Gadget      | 12       |
        """);

    [Fact]
    public void a_set_verification_from_a_table_literal_renders_the_grid()
    {
        var spec = render(f =>
        {
            twoItems(f);
            f.TheInventoryShouldBe("""
                | Sku     | ProductName | Quantity |
                | SKU-002 | Gadget      | 12       |
                | SKU-001 | Widget      | 90       |
                """);
        });

        var grid = spec.Steps.Single(s => s.StepText.Contains("the inventory should be"))
            .SetVerification.ShouldNotBeNull();

        grid.Columns.ShouldBe(new[] { "Sku", "ProductName", "Quantity" });
        grid.Rows.Count.ShouldBe(2);
        grid.Rows.ShouldAllBe(r => r.AllCellsOk);
    }

    [Fact]
    public void a_wrong_value_is_one_failed_cell_in_the_projected_grid_too()
    {
        var spec = render(f =>
        {
            twoItems(f);
            f.TheInventoryShouldBe("""
                | Sku     | ProductName | Quantity |
                | SKU-001 | Widget      | 85       |
                | SKU-002 | Gadget      | 12       |
                """);
        });

        var grid = spec.Steps.Single(s => s.StepText.Contains("the inventory should be"))
            .SetVerification.ShouldNotBeNull();

        grid.Rows[0].AllCellsOk.ShouldBeFalse();
        grid.Rows[0].Cells.Single(c => c.Column == "Quantity").DisplayText
            .ShouldBe("expected '85', got '90'");
        grid.Rows[1].AllCellsOk.ShouldBeTrue();
    }

    [Fact]
    public void a_missing_row_and_an_extra_row_read_the_same_way_in_both_lanes()
    {
        var spec = render(f =>
        {
            f.TheInventoryIs("""
                | Sku     | ProductName | Quantity |
                | SKU-002 | Gadget      | 12       |
                """);

            f.TheInventoryShouldBe("""
                | Sku     | ProductName | Quantity |
                | SKU-001 | Widget      | 90       |
                """);
        });

        var grid = spec.Steps.Single(s => s.StepText.Contains("the inventory should be"))
            .SetVerification.ShouldNotBeNull();

        grid.Rows.Select(r => r.RowType)
            .ShouldBe(new[] { SetVerificationRowType.Missing, SetVerificationRowType.Extra });
        grid.Rows[0].Cells.Select(c => c.DisplayText).ShouldBe(new[] { "SKU-001", "Widget", "90" });
        grid.Rows[1].Cells.Select(c => c.DisplayText).ShouldBe(new[] { "SKU-002", "Gadget", "12" });
    }

    [Fact]
    public void an_ordered_set_reports_the_reordering_from_C_too()
    {
        var spec = render(f =>
        {
            twoItems(f);
            f.TheInventoryInOrderShouldBe("""
                | Sku     | ProductName | Quantity |
                | SKU-002 | Gadget      | 12       |
                | SKU-001 | Widget      | 90       |
                """);
        });

        var grid = spec.Steps.Single(s => s.StepText.Contains("in order should be"))
            .SetVerification.ShouldNotBeNull();

        grid.Rows[1].RowType.ShouldBe(SetVerificationRowType.OutOfOrder);
        grid.Rows[1].Description.ShouldContain("found at position 1");
    }

    [Fact]
    public void a_set_of_plain_values_needs_no_column_named_at_the_call_site()
    {
        var spec = render(f =>
        {
            f.TheTagsAre("""
                | tag     |
                | urgent  |
                | shipped |
                """);

            f.TheTagsShouldBe("""
                | tag     |
                | shipped |
                | urgent  |
                """);
        });

        var grid = spec.Steps.Single(s => s.StepText.Contains("the tags should be"))
            .SetVerification.ShouldNotBeNull();

        grid.Columns.ShouldBe(new[] { "tag" });
        grid.Rows.ShouldAllBe(r => r.AllCellsOk);
    }

    [Fact]
    public void a_disagreeing_set_costs_the_projected_spec_its_verdict()
    {
        var spec = render(f =>
        {
            twoItems(f);
            f.TheInventoryShouldBe("""
                | Sku     | ProductName | Quantity |
                | SKU-001 | Widget      | 85       |
                | SKU-002 | Gadget      | 12       |
                """);
        });

        // The grid is the verdict. A projected set verification does not throw — the same choice
        // the table lane makes, so a spec reports every disagreement it found rather than the first.
        spec.Counts.Wrongs.ShouldBeGreaterThan(0);
    }
}
