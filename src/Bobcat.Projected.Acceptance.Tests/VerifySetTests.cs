using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;

namespace Bobcat.Projected.Acceptance.Tests;

public record InventoryItem(string Sku, string ProductName, int Quantity);

/// <summary>
/// <c>VerifySet.feature</c> and the old <c>ProjectedVerifySetTests</c>, off <c>Fixture</c>: a plain
/// grammar builds its rows with <see cref="TableRunner.BuildRows{T}"/> and verifies the set with
/// <see cref="SetVerificationComparer.Verify"/>, which is what <c>Fixture.BuildRows</c> and
/// <c>Fixture.VerifySet</c> were always calling underneath.
/// </summary>
public class VerifySetTests
{
    public class InventoryGrammar
    {
        private readonly List<InventoryItem> _inventory = new();
        private readonly List<string> _tags = new();

        [Given("the inventory is")]
        internal void TheInventoryIs(StepTable table) => _inventory.AddRange(TableRunner.BuildRows<InventoryItem>(table));

        [Then("the inventory should be")]
        internal void TheInventoryShouldBe(StepTable expected)
            => SetVerificationComparer.Verify(_inventory, expected, keyColumns: "Sku");

        [Then("the inventory in order should be")]
        internal void TheInventoryInOrderShouldBe(StepTable expected)
            => SetVerificationComparer.Verify(_inventory, expected, keyColumns: "Sku", ordered: true);

        [Given("the tags are")]
        internal void TheTagsAre(StepTable table) => _tags.AddRange(table.Rows.Select(r => r[0]));

        /// <summary>A one-column table over plain values needs no column named.</summary>
        [Then("the tags should be")]
        internal void TheTagsShouldBe(StepTable expected) => SetVerificationComparer.Verify(_tags, expected);
    }

    private readonly InventoryGrammar _grammar = new();

    private void twoItems() => _grammar.TheInventoryIs("""
        | Sku     | ProductName | Quantity |
        | SKU-001 | Widget      | 90       |
        | SKU-002 | Gadget      | 12       |
        """);

    private static SetVerificationRender gridOf(SpecRender render, string step)
        => render.Step(step).SetVerification.ShouldNotBeNull();

    [Fact]
    public void a_set_the_step_verifies_itself_renders_one_grid()
    {
        var (recording, render) = Projected.Run("one grid", () =>
        {
            twoItems();
            _grammar.TheInventoryShouldBe("""
                | Sku     | ProductName | Quantity |
                | SKU-002 | Gadget      | 12       |
                | SKU-001 | Widget      | 90       |
                """);
        });

        recording.Step("the inventory should be").Status.ShouldBe(ResultStatus.success);

        var grid = gridOf(render, "the inventory should be");
        grid.Columns.ShouldBe(["Sku", "ProductName", "Quantity"]);
        grid.Rows.Count.ShouldBe(2);
        grid.Rows.ShouldAllBe(r => r.AllCellsOk);
    }

    [Fact]
    public void a_key_column_turns_a_wrong_value_into_one_failed_cell()
    {
        var (recording, render) = Projected.Run("wrong value", () =>
        {
            twoItems();
            _grammar.TheInventoryShouldBe("""
                | Sku     | ProductName | Quantity |
                | SKU-001 | Widget      | 85       |
                | SKU-002 | Gadget      | 12       |
                """);
        });

        var step = recording.Step("the inventory should be");
        step.Status.ShouldBe(ResultStatus.failed);

        var grid = gridOf(render, "the inventory should be");
        grid.Rows[0].AllCellsOk.ShouldBeFalse();
        var quantity = grid.Rows[0].Cells.Single(c => c.Column == "Quantity");
        quantity.Expected.ShouldBe("85");
        quantity.Actual.ShouldBe("90");
        quantity.DisplayText.ShouldBe("expected '85', got '90'");
        grid.Rows[1].AllCellsOk.ShouldBeTrue();

        // The row was still matched, so neither marker cell appears.
        step.Cells.ShouldNotContain(c => c.Name == "missing-row" || c.Name == "extra-row");
    }

    [Fact]
    public void a_missing_row_and_an_extra_row_each_show_their_own_values()
    {
        var (recording, render) = Projected.Run("missing and extra", () =>
        {
            _grammar.TheInventoryIs("""
                | Sku     | ProductName | Quantity |
                | SKU-002 | Gadget      | 12       |
                """);
            _grammar.TheInventoryShouldBe("""
                | Sku     | ProductName | Quantity |
                | SKU-001 | Widget      | 90       |
                """);
        });

        recording.Step("the inventory should be").Status.ShouldBe(ResultStatus.failed);

        var grid = gridOf(render, "the inventory should be");
        grid.Rows.Select(r => r.RowType).ShouldBe([SetVerificationRowType.Missing, SetVerificationRowType.Extra]);
        grid.Rows[0].Cells.Select(c => c.DisplayText).ShouldBe(["SKU-001", "Widget", "90"]);
        grid.Rows[1].Cells.Select(c => c.DisplayText).ShouldBe(["SKU-002", "Gadget", "12"]);
    }

    [Fact]
    public void ordered_is_checked_after_matching()
    {
        var (recording, render) = Projected.Run("reordered", () =>
        {
            _grammar.TheInventoryIs("""
                | Sku     | ProductName | Quantity |
                | SKU-002 | Gadget      | 12       |
                | SKU-001 | Widget      | 90       |
                """);
            _grammar.TheInventoryInOrderShouldBe("""
                | Sku     | ProductName | Quantity |
                | SKU-001 | Widget      | 90       |
                | SKU-002 | Gadget      | 12       |
                """);
        });

        var step = recording.Step("the inventory in order should be");
        step.Status.ShouldBe(ResultStatus.failed);

        // One reordering, not two rows of wrong values.
        step.Cells.ShouldNotContain(c => c.Name == "missing-row" || c.Name == "extra-row");

        var grid = gridOf(render, "the inventory in order should be");
        grid.Rows[1].RowType.ShouldBe(SetVerificationRowType.OutOfOrder);
        grid.Rows[1].Description.ShouldNotBeNull().ShouldContain("found at position 1");
    }

    [Fact]
    public void a_set_of_plain_values_infers_its_column_from_a_one_column_table()
    {
        var (recording, render) = Projected.Run("tags", () =>
        {
            _grammar.TheTagsAre("""
                | tag     |
                | urgent  |
                | shipped |
                """);
            _grammar.TheTagsShouldBe("""
                | tag     |
                | shipped |
                | urgent  |
                """);
        });

        recording.Step("the tags should be").Status.ShouldBe(ResultStatus.success);

        var grid = gridOf(render, "the tags should be");
        grid.Columns.ShouldBe(["tag"]);
        grid.Rows.ShouldAllBe(r => r.AllCellsOk);

        // Not compared against Length and Chars.
        recording.Step("the tags should be").Cells.ShouldNotContain(c => c.Name == "Length");
    }

    [Fact]
    public void a_header_with_no_rows_under_it_still_names_the_columns()
    {
        var (recording, render) = Projected.Run("empty", () =>
        {
            _grammar.TheInventoryIs("""
                | Sku     | ProductName | Quantity |
                | SKU-001 | Widget      | 90       |
                """);
            _grammar.TheInventoryShouldBe("""
                | Sku | ProductName | Quantity |
                """);
        });

        // A row the document doesn't describe is a wrong, not an exception (bobcat#471).
        recording.Step("the inventory should be").Status.ShouldBe(ResultStatus.failed);
        recording.GatheredFailures().ShouldNotBeNull().ShouldContain("the inventory should be");

        var grid = gridOf(render, "the inventory should be");
        grid.Columns.ShouldBe(["Sku", "ProductName", "Quantity"]);

        var extra = grid.Rows.Single();
        extra.RowType.ShouldBe(SetVerificationRowType.Extra);
        extra.Cells.Select(c => c.DisplayText).ShouldBe(["SKU-001", "Widget", "90"]);
    }

    /// <summary>
    /// An extra row is a disagreement with the document, so it's <c>failed</c>, the same verdict a
    /// missing row or a wrong value gets. Only a row whose code threw is <c>error</c>
    /// (<see cref="DecisionTableTests.a_throwing_row_errors_the_step"/>).
    /// </summary>
    [Fact]
    public void an_extra_row_alone_fails_the_step()
    {
        var (recording, _) = Projected.Run("extra alone", () =>
        {
            twoItems();
            _grammar.TheInventoryShouldBe("""
                | Sku     | ProductName | Quantity |
                | SKU-001 | Widget      | 90       |
                """);
        });

        recording.Step("the inventory should be").Status.ShouldBe(ResultStatus.failed);
    }

    [Fact]
    public void a_disagreeing_set_costs_the_spec_its_verdict_without_throwing()
    {
        var (recording, render) = Projected.Run("verdict", () =>
        {
            twoItems();

            // Does not throw: a spec reports every disagreement it found rather than the first.
            _grammar.TheInventoryShouldBe("""
                | Sku     | ProductName | Quantity |
                | SKU-001 | Widget      | 85       |
                | SKU-002 | Gadget      | 12       |
                """);
        });

        render.Counts.Wrongs.ShouldBeGreaterThan(0);
        recording.GatheredFailures().ShouldNotBeNull().ShouldContain("the inventory should be");
    }

    [Fact]
    public void a_missing_row_alone_still_costs_the_scenario_its_verdict()
    {
        var (recording, _) = Projected.Run("missing alone", () =>
        {
            _grammar.TheInventoryIs("""
                | Sku     | ProductName | Quantity |
                | SKU-001 | Widget      | 90       |
                """);
            _grammar.TheInventoryShouldBe("""
                | Sku     | ProductName | Quantity |
                | SKU-001 | Widget      | 90       |
                | SKU-002 | Gadget      | 12       |
                """);
        });

        // A missing row is the one marker cell whose status is neither `failed` nor `invalid`.
        recording.GatheredFailures().ShouldNotBeNull().ShouldContain("the inventory should be");
    }
}
