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

/// <summary>
/// A dotted column binds through nested properties (issue #411) — the lookup four callers share:
/// the two shipped event-store grammars, <c>Fixture.VerifyObject</c>, and the Wolverine side's
/// event and HTTP-response assertions.
/// </summary>
public class NestedPropertyCellsTests
{
    public record Address(string Line1, string City, [property: Header("zip")] string Postcode);

    public record Customer(string Name, Address? Address);

    public record Order(string Id, Customer Customer, Order? Parent = null);

    private static readonly Order _order = new(
        "ORD-1", new Customer("Hannah", new Address("1 High St", "Austin", "78701")));

    private static TableRun compare(Order order, string table)
        => PropertyCells.Cells(order, StepTable.Parse(table));

    [Fact]
    public void a_dotted_column_resolves_through_the_graph()
    {
        var run = compare(_order, """
            | Id    | Customer.Name | Customer.Address.City |
            | ORD-1 | Hannah        | Austin                |
            """);

        run.Succeeded.ShouldBeTrue();
        run.Cells.Single(c => c.Name == "Customer.Address.City").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void a_dotted_column_that_disagrees_reads_like_any_other_cell()
    {
        compare(_order, """
            | Customer.Address.City |
            | Dallas                |
            """)
            .Cells.ShouldHaveSingleItem().DisplayText.ShouldBe("expected 'Dallas', got 'Austin'");
    }

    [Fact]
    public void header_titling_applies_at_every_depth_not_only_the_top()
    {
        // Postcode carries [Header("zip")], so the header is how a spec addresses it — and a
        // renamed property has to stay addressable nested as well as at the top.
        compare(_order, """
            | Customer.Address.zip |
            | 78701                |
            """)
            .Cells.ShouldHaveSingleItem().Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void a_segment_that_does_not_exist_is_invalid_and_names_what_is_there_at_that_depth()
    {
        var cell = compare(_order, """
            | Customer.Address.Town |
            | Austin                |
            """).Cells.ShouldHaveSingleItem();

        cell.Status.ShouldBe(ResultStatus.invalid);

        // Named at the depth that FAILED. The subject's own property list would be the least
        // useful half of the sentence — the author's typo is in the last segment.
        cell.DisplayText.ShouldBe("no 'Town' on Address — it has City, Line1, zip");
    }

    [Fact]
    public void a_null_partway_along_the_path_is_the_subject_disagreeing_not_the_spec_being_wrong()
    {
        var order = new Order("ORD-2", new Customer("Ada", null));

        var cell = compare(order, """
            | Customer.Address.City |
            | Austin                |
            """).Cells.ShouldHaveSingleItem();

        // `failed`, not `invalid`: the property exists and the path is legal — the graph was empty.
        // Only run time can tell, which is exactly the difference between the two statuses.
        cell.Status.ShouldBe(ResultStatus.failed);
        cell.Note.ShouldBe("Customer.Address was null");
        cell.DisplayText.ShouldBe("expected 'Austin', got 'NULL' (Customer.Address was null)");
    }

    [Fact]
    public void a_null_at_the_end_of_the_path_is_an_ordinary_comparison()
    {
        // The distinction above only applies PARTWAY along: a null leaf is a value, and NULL is a
        // cell expression a spec can legitimately assert.
        var order = new Order("ORD-2", new Customer("Ada", null));

        compare(order, """
            | Customer.Address |
            | NULL             |
            """)
            .Cells.ShouldHaveSingleItem().Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void an_indexed_column_says_to_use_a_set_verification_instead()
    {
        var cell = compare(_order, """
            | Customer.Addresses[0].City |
            | Austin                     |
            """).Cells.ShouldHaveSingleItem();

        cell.Status.ShouldBe(ResultStatus.invalid);
        cell.DisplayText.ShouldContain("set verification");
    }

    [Fact]
    public void depth_is_bounded_so_a_cycle_cannot_hang_a_comparison()
    {
        var path = string.Join('.', Enumerable.Repeat("Parent", PropertyCells.MaxDepth + 1)) + ".Id";

        var result = PropertyCells.Resolve(_order, path);

        result.Kind.ShouldBe(PropertyCells.PathResultKind.NoSuchProperty);
        result.Message.ShouldContain($"the limit is {PropertyCells.MaxDepth}");
    }

    [Fact]
    public void an_undotted_column_behaves_exactly_as_it_did()
    {
        // The whole point of adding this to the shared lookup is that nothing else changes.
        var result = PropertyCells.Resolve(_order, "Id");

        result.Kind.ShouldBe(PropertyCells.PathResultKind.Found);
        result.Value.ShouldBe("ORD-1");
    }
}
