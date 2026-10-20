using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;

namespace Bobcat.Projected.Acceptance.Tests;

public record Detail(string Name, decimal Amount);

/// <summary>
/// A set's columns are its result type's properties, so a title goes on the property. On a record
/// that means targeting the property explicitly.
/// </summary>
// #region ledger-record
public record Ledger(
    [property: Header("Line Item")] string Name,
    [property: Header("The Amount")] decimal Amount);
// #endregion ledger-record

public enum Grade
{
    Gold,
    Silver,
    Bronze
}

/// <summary>
/// <c>SetVerification.feature</c> from the projected lane. <c>[SetVerification(KeyColumns, Ordered,
/// Column)]</c> takes its expected rows from the generator, so no C# test can call one; its settings
/// become arguments to <see cref="SetVerificationComparer.Verify"/>, and the expected rows arrive as
/// a table literal.
/// </summary>
public class SetVerificationTests
{
    public class DetailsGrammar
    {
        public readonly List<Detail> Details = new();
        public readonly List<string> Names = new();
        public readonly List<Ledger> Ledger = new();
        public readonly List<string> Roster = new();

        [Given("the details are")]
        internal void TheDetailsAre(StepTable table) => Details.AddRange(TableRunner.BuildRows<Detail>(table));

        [Then("the details should be")]
        internal void TheDetailsShouldBe(StepTable expected)
            => SetVerificationComparer.Verify(Details, expected, keyColumns: "Name");

        [Then("the details in order should be")]
        internal void TheDetailsInOrderShouldBe(StepTable expected)
            => SetVerificationComparer.Verify(Details, expected, keyColumns: "Name", ordered: true);

        [Given("the names are")]
        internal void TheNamesAre(StepTable table) => Names.AddRange(table.Rows.Select(r => r[0]));

        /// <summary>A set of plain values: <c>column</c> names the one column they are compared under.</summary>
        [Then("the names in order should be")]
        internal void TheNamesInOrderShouldBe(StepTable expected)
            => SetVerificationComparer.Verify(Names, expected, ordered: true, column: "Name");

        [Given("the ledger is")]
        internal void TheLedgerIs(StepTable table)
            => Ledger.AddRange(TableRunner.BuildRows<Detail>(table).Select(d => new Ledger(d.Name, d.Amount)));

        /// <summary>The key column is named as the document writes it: by its title.</summary>
        [Then("the ledger should be")]
        internal void TheLedgerShouldBe(StepTable expected)
            => SetVerificationComparer.Verify(Ledger, expected, keyColumns: "Line Item");

        [Given("the roster is")]
        internal async Task TheRosterIs(StepTable table) => await TableRunner.Run(this, nameof(addPlayer), table);

        private void addPlayer([Header("Player Name")] string player, Grade grade = Grade.Bronze)
            => Roster.Add($"{player}:{grade}");

        [Then("the roster reads {roster}")]
        internal void TheRosterReads(string roster) => SpecAssert.Check("roster", string.Join("|", Roster), roster);
    }

    private readonly DetailsGrammar _grammar = new();

    private void cordAndDrill() => _grammar.TheDetailsAre("""
        | name  | amount |
        | Cord  | 100    |
        | Drill | 200    |
        """);

    [Fact]
    public void an_unordered_set_ignores_the_order_the_rows_arrive_in()
    {
        var (recording, _) = Projected.Run("unordered", () =>
        {
            cordAndDrill();
            _grammar.TheDetailsShouldBe("""
                | Name  | Amount |
                | Drill | 200    |
                | Cord  | 100    |
                """);
        });

        recording.Step("the details should be").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void an_ordered_set_passes_when_the_order_agrees()
    {
        var (recording, _) = Projected.Run("ordered", () =>
        {
            cordAndDrill();
            _grammar.TheDetailsInOrderShouldBe("""
                | Name  | Amount |
                | Cord  | 100    |
                | Drill | 200    |
                """);
        });

        recording.Step("the details in order should be").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void an_ordered_set_reports_the_row_that_turned_up_early()
    {
        var (recording, render) = Projected.Run("out of order", () =>
        {
            cordAndDrill();
            _grammar.TheDetailsInOrderShouldBe("""
                | Name  | Amount |
                | Drill | 200    |
                | Cord  | 100    |
                """);
        });

        var step = recording.Step("the details in order should be");
        step.Status.ShouldBe(ResultStatus.failed);

        var marker = step.Cells.Single(c => c.Name == SetVerificationComparer.OutOfOrderCell);
        marker.RowIndex.ShouldBe(1);
        marker.DisplayText.ShouldContain("found at position 1");

        // The row's own values are right; it is the position that disagrees.
        var grid = render.Step("the details in order should be").SetVerification.ShouldNotBeNull();
        grid.Rows[1].RowType.ShouldBe(SetVerificationRowType.OutOfOrder);
        grid.Rows[1].Cells.ShouldAllBe(c => c.Status == ResultStatus.success);
    }

    [Fact]
    public void a_set_of_plain_values_is_compared_under_the_column_the_grammar_names()
    {
        var (recording, render) = Projected.Run("plain values", () =>
        {
            _grammar.TheNamesAre("""
                | name   |
                | Luke   |
                | Han    |
                | Chewie |
                """);
            _grammar.TheNamesInOrderShouldBe("""
                | Name   |
                | Luke   |
                | Chewie |
                | Han    |
                """);
        });

        var step = recording.Step("the names in order should be");
        render.Step("the names in order should be").SetVerification.ShouldNotBeNull().Columns.ShouldBe(["Name"]);

        // Luke is in place; Chewie and Han are swapped, so the second of them is the one reported.
        step.Cells.Single(c => c.RowIndex == 0 && c.Name == "Name" && c.Status != ResultStatus.ok)
            .Status.ShouldBe(ResultStatus.success);
        step.Cells.Single(c => c.Name == SetVerificationComparer.OutOfOrderCell).RowIndex.ShouldBe(2);

        // Not compared against the properties of `string`.
        step.Cells.ShouldNotContain(c => c.Name == "Length");
    }

    [Fact]
    public async Task a_column_can_carry_a_header_of_its_own()
    {
        var (recording, render) = await Projected.RunAsync("header", async () =>
        {
            await _grammar.TheRosterIs("""
                | Player Name | grade  |
                | Willy Mays  | Gold   |
                | Nolan Ryan  | Silver |
                """);
            _grammar.TheRosterReads("Willy Mays:Gold|Nolan Ryan:Silver");
        });

        recording.Step("the roster reads").Status.ShouldBe(ResultStatus.success);

        // The grid shows the document's own heading, not the parameter name.
        render.Step("the roster is").SetVerification.ShouldNotBeNull().Columns.ShouldBe(["Player Name", "grade"]);
    }

    [Fact]
    public async Task an_optional_column_may_be_left_out_of_the_table()
    {
        var (recording, _) = await Projected.RunAsync("optional column", async () =>
        {
            await _grammar.TheRosterIs("""
                | Player Name |
                | Willy Mays  |
                """);

            // The parameter's own default applies, not default(Grade), which would be Gold.
            _grammar.TheRosterReads("Willy Mays:Bronze");
        });

        recording.Step("the roster reads").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void a_header_with_no_rows_under_it_still_names_the_columns()
    {
        var (recording, render) = Projected.Run("empty", () =>
        {
            _grammar.TheDetailsAre("""
                | name | amount |
                | Cord | 100    |
                """);
            _grammar.TheDetailsShouldBe("""
                | Name | Amount |
                """);
        });

        // A row the document doesn't describe is a wrong, not an exception (bobcat#471).
        recording.Step("the details should be").Status.ShouldBe(ResultStatus.failed);
        recording.GatheredFailures().ShouldNotBeNull().ShouldContain("the details should be");

        // "The set should be empty" is a real expectation, and the header says what the columns are.
        var grid = render.Step("the details should be").SetVerification.ShouldNotBeNull();
        grid.Columns.ShouldBe(["Name", "Amount"]);

        var extra = grid.Rows.Single();
        extra.RowType.ShouldBe(SetVerificationRowType.Extra);
        extra.Cells.Select(c => c.DisplayText).ShouldBe(["Cord", "100"]);
    }

    private void ledger() => _grammar.TheLedgerIs("""
        | name  | amount |
        | Cord  | 100    |
        | Drill | 200    |
        """);

    [Fact]
    public void a_property_titled_for_the_document_is_compared_under_its_title()
    {
        var (recording, render) = Projected.Run("titled", () =>
        {
            ledger();
            // #region ledger-call
            _grammar.TheLedgerShouldBe("""
                | Line Item | The Amount |
                | Drill     | 200        |
                | Cord      | 100        |
                """);
            // #endregion ledger-call
        });

        recording.Step("the ledger should be").Status.ShouldBe(ResultStatus.success);

        var grid = render.Step("the ledger should be").SetVerification.ShouldNotBeNull();
        grid.Columns.ShouldBe(["Line Item", "The Amount"]);

        // Matched on "Line Item", so the key column named the column the document writes.
        grid.Rows.Select(r => r.Cells.Single(c => c.Column == "The Amount").Actual).ShouldBe(["200", "100"]);
    }

    [Fact]
    public void a_title_replaces_the_property_name_rather_than_aliasing_it()
    {
        var (recording, render) = Projected.Run("not an alias", () =>
        {
            _grammar.TheLedgerIs("""
                | name | amount |
                | Cord | 100    |
                """);
            _grammar.TheLedgerShouldBe("""
                | Name | Amount |
                | Cord | 100    |
                """);
        });

        // The cost of titling a column: the property name now matches nothing.
        recording.Step("the ledger should be").Status.ShouldBe(ResultStatus.failed);
        render.Step("the ledger should be").SetVerification.ShouldNotBeNull().Rows.Select(r => r.RowType)
            .ShouldBe([SetVerificationRowType.Missing, SetVerificationRowType.Extra]);
    }
}
