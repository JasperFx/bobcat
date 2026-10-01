using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// The comparison behind "this document has these values" — one object against one table row, as a
/// grid with a verdict per column rather than a sentence listing the failures (issue #384).
/// </summary>
public class PropertyCellsTests
{
    public record Queue(int AwaitingConfirmation, int Confirmed, string Name);

    private static TableRun compare(Queue queue, string table)
        => PropertyCells.Cells(queue, StepTable.Parse(table));

    [Fact]
    public void every_column_the_row_names_gets_a_cell_of_its_own()
    {
        var run = compare(new Queue(1, -1, "weekday"), """
            | AwaitingConfirmation | Confirmed | Name    |
            | 0                    | 0         | weekday |
            """);

        run.Columns.ShouldBe(new[] { "AwaitingConfirmation", "Confirmed", "Name" });
        run.Succeeded.ShouldBeFalse();

        // Three columns, three verdicts — two red and one green. Flattened into a message, the green
        // one was invisible and the two red ones had to be read out of a sentence.
        run.Cells.Single(c => c.Name == "AwaitingConfirmation").DisplayText
            .ShouldBe("expected '0', got '1'");
        run.Cells.Single(c => c.Name == "Confirmed").DisplayText.ShouldBe("expected '0', got '-1'");
        run.Cells.Single(c => c.Name == "Name").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void an_agreeing_row_succeeds_and_still_shows_its_values()
    {
        var run = compare(new Queue(0, 2, "weekend"), """
            | AwaitingConfirmation | Confirmed | Name    |
            | 0                    | 2         | weekend |
            """);

        run.Succeeded.ShouldBeTrue();
        run.Cells.Count.ShouldBe(3);
        run.Cells.ShouldAllBe(c => c.Status == ResultStatus.success);
    }

    [Fact]
    public void only_the_columns_the_row_names_are_compared()
    {
        // The document decides what it cares about, the same rule KeyColumns follows. A property the
        // table does not mention is not part of the claim.
        var run = compare(new Queue(0, 2, "weekend"), """
            | Confirmed |
            | 2         |
            """);

        run.Succeeded.ShouldBeTrue();
        run.Cells.ShouldHaveSingleItem().Name.ShouldBe("Confirmed");
    }

    [Fact]
    public void a_column_naming_no_property_is_invalid_and_says_what_there_is()
    {
        // Not a disagreement — the specification asked about something that does not exist, which is
        // usually a typo or a renamed property, so the message names what IS there.
        var cell = compare(new Queue(0, 2, "weekend"), """
            | Confirmd |
            | 2        |
            """).Cells.ShouldHaveSingleItem();

        cell.Status.ShouldBe(ResultStatus.invalid);
        cell.DisplayText.ShouldContain("no 'Confirmd' on Queue");
        cell.DisplayText.ShouldContain("Confirmed");
    }

    [Fact]
    public void a_cell_that_cannot_be_read_as_the_property_type_is_invalid_too()
    {
        compare(new Queue(0, 2, "weekend"), """
            | Confirmed |
            | ever so   |
            """).Cells.ShouldHaveSingleItem().Status.ShouldBe(ResultStatus.invalid);
    }

    [Fact]
    public void the_disagreeing_columns_are_named_for_a_message_that_has_to_be_short()
    {
        var run = compare(new Queue(1, -1, "weekday"), """
            | AwaitingConfirmation | Confirmed | Name    |
            | 0                    | 0         | weekday |
            """);

        // The grid says it at length; a CI log tailing one line still needs to know where to look.
        PropertyCells.Disagreeing(run).ShouldBe(new[] { "AwaitingConfirmation", "Confirmed" });
    }

    public record Titled([property: Header("How Many")] int Quantity);

    [Fact]
    public void a_property_titled_for_the_document_is_compared_under_its_title_here_too()
    {
        // The same ColumnNames authority the set verification uses, so a titled property is titled
        // wherever a table names it.
        PropertyCells.Cells(new Titled(7), StepTable.Parse("""
            | How Many |
            | 7        |
            """)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void every_cell_lands_on_the_one_row_there_is()
    {
        compare(new Queue(0, 2, "weekend"), """
            | AwaitingConfirmation | Confirmed |
            | 0                    | 2         |
            """).Cells.ShouldAllBe(c => c.RowIndex == 0);
    }
}
