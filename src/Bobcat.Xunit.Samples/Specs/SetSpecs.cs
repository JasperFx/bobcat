using Bobcat.Xunit.Samples.Grammars;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// Storyteller's Sets samples from the C# side. Seven of these nine fail on purpose — a wrong cell,
/// an extra row, a missing row, a reordering — because the samples exist to show what each outcome
/// looks like in the grid.
/// </summary>
/// <remarks>
/// The Gherkin lane recreates the same documents in
/// <c>Bobcat.Gherkin.Samples/Features/Sets.feature</c> against the declarative
/// <c>[SetVerification]</c> form. Reading the two side by side is the point: the grids are identical,
/// because the comparison is.
/// </remarks>
[BobcatFeature("Sets"), BobcatScenario]
public class SetSpecs
{
    private readonly SetsGrammar _sets = new();

    /// <summary>
    /// Samples/Specs/Sets/Object_Sets.md — "This declaration is all out of order with the actual
    /// data, but that's okay in this case".
    /// </summary>
    [Fact]
    public void an_unordered_set_in_a_different_order_from_the_data()
    {
        _sets.TheInvoiceDetailsAre("""
            | Amount | Date    | Name       |
            | 100    | TODAY   | The Shirts |
            | 200    | TODAY-1 | The Pants  |
            | 10     | TODAY-2 | Socks      |
            """);

        _sets.TheUnorderedDetailsShouldBe("""
            | Amount | Date    | Name       |
            | 10     | TODAY-2 | Socks      |
            | 200    | TODAY-1 | The Pants  |
            | 100    | TODAY   | The Shirts |
            """);
    }

    /// <summary>
    /// The same document — "All Cells are Part of the Evaluation. This time, some of the cell values
    /// are wrong". Two wrong cells, and a wrong <c>Name</c> is a missing row beside an extra one
    /// because <c>Name</c> is the key column.
    /// </summary>
    [Fact]
    public void every_cell_is_part_of_the_evaluation()
    {
        _sets.TheInvoiceDetailsAre("""
            | Amount | Date    | Name       |
            | 100    | TODAY   | The Shirts |
            | 200    | TODAY-1 | The Pants  |
            | 10     | TODAY-2 | Socks      |
            """);

        _sets.TheUnorderedDetailsShouldBe("""
            | Amount | Date    | Name       |
            | 11     | TODAY-2 | Socks      |
            | 200    | TODAY-5 | The Pants  |
            | 100    | TODAY   | Sweatpants |
            """);
    }

    /// <summary>
    /// The same document — "Now, let's do the same results where order matters. The table below will
    /// fail because the ordering is wrong". One reordering, not three rows of wrong values: order is
    /// checked after matching rather than instead of it.
    /// </summary>
    [Fact]
    public void an_ordered_set_whose_rows_are_in_the_wrong_order()
    {
        _sets.TheInvoiceDetailsAre("""
            | Amount | Date    | Name       |
            | 100    | TODAY   | The Shirts |
            | 200    | TODAY-1 | The Pants  |
            | 10     | TODAY-2 | Socks      |
            """);

        _sets.TheOrderedDetailsShouldBe("""
            | Amount | Date    | Name       |
            | 10     | TODAY-2 | Socks      |
            | 200    | TODAY-1 | The Pants  |
            | 100    | TODAY   | The Shirts |
            """);
    }

    /// <summary>Samples/Specs/Sets/Data_Tables.md — "Happy Path".</summary>
    [Fact]
    public void every_row_in_the_database_is_accounted_for()
    {
        theDatabaseHasThreeCities();

        _sets.TheRowsShouldBe("""
            | City        | Distance | Zip   |
            | Austin      | 5        | 78750 |
            | Jasper      | 600      | 64755 |
            | Bentonville | 550      | 72712 |
            """);
    }

    /// <summary>The same document — "Extra Rows Detected from the Database".</summary>
    [Fact]
    public void the_database_has_a_row_the_specification_does_not()
    {
        theDatabaseHasThreeCities();

        _sets.TheRowsShouldBe("""
            | City   | Distance | Zip   |
            | Austin | 5        | 78750 |
            | Jasper | 600      | 64755 |
            """);
    }

    /// <summary>The same document — "Missing Rows in the Database".</summary>
    [Fact]
    public void the_specification_expects_a_row_the_database_does_not_have()
    {
        theDatabaseHasThreeCities();

        _sets.TheRowsShouldBe("""
            | City        | Distance | Zip   |
            | Austin      | 5        | 78750 |
            | Jasper      | 600      | 64755 |
            | Bentonville | 550      | 72712 |
            | Joplin      | 575      | 64801 |
            """);
    }

    /// <summary>
    /// The same document — "Mismatch in Rows". The key column itself disagrees, so it reads as one
    /// missing row and one extra rather than as a row with a wrong cell. Naming fewer key columns is
    /// what turns a mismatch back into a cell-level disagreement.
    /// </summary>
    [Fact]
    public void a_row_whose_key_column_disagrees()
    {
        theDatabaseHasThreeCities();

        _sets.TheRowsShouldBe("""
            | City        | Distance | Zip   |
            | Round Rock  | 5        | 78750 |
            | Jasper      | 600      | 64755 |
            | Bentonville | 550      | 72712 |
            """);
    }

    /// <summary>
    /// Samples/Specs/Sets/String_Lists.md — "The order is incorrect and this declaration should
    /// fail". A set of plain strings, with no wrapper record anywhere.
    /// </summary>
    [Fact]
    public void a_set_of_names_in_the_wrong_order()
    {
        theNamesAreLukeHanChewie();

        _sets.TheNamesShouldBe("""
            | Name   |
            | Luke   |
            | Chewie |
            | Han    |
            """);
    }

    /// <summary>
    /// The same document — "Chewie got lost, so this fails too" and "Leia wasn't with them at the
    /// time", in one scenario: a row the list does not have beside a name it does.
    /// </summary>
    [Fact]
    public void a_set_of_names_missing_one_and_expecting_one_that_is_not_there()
    {
        theNamesAreLukeHanChewie();

        _sets.TheNamesShouldBe("""
            | Name |
            | Luke |
            | Han  |
            | Leia |
            """);
    }

    private void theDatabaseHasThreeCities() => _sets.TheCitiesAre("""
        | City        | Distance | Zip   |
        | Austin      | 5        | 78750 |
        | Jasper      | 600      | 64755 |
        | Bentonville | 550      | 72712 |
        """);

    private void theNamesAreLukeHanChewie() => _sets.TheNamesAre("""
        | Name   |
        | Luke   |
        | Han    |
        | Chewie |
        """);
}
