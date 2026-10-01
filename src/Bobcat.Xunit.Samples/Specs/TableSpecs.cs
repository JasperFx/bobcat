using Bobcat.Xunit.Samples.Grammars;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// Storyteller's table samples from the C# side: the tabular data written as a table literal in the
/// test itself, against the same grammar body a <c>.feature</c> file would bind to.
/// </summary>
[BobcatFeature("Tables"), BobcatScenario]
public class TableSpecs
{
    private readonly RosterGrammar _roster = new();

    /// <summary>
    /// Samples/Specs/Tables/Table_with_Options.md, with the `position` column present on two rows
    /// and left out of the third — the same optional-column shape the Gherkin lane reads.
    /// </summary>
    [Fact]
    public void a_table_of_setup_data()
    {
        _roster.TheRosterIs("""
            | player       | position |
            | Nolan Ryan   | Pitcher  |
            | Willy Mays   | Outfield |
            | Johnny Bench | Catcher  |
            """);

        _roster.TheRosterReads("Nolan Ryan (Pitcher), Willy Mays (Outfield), Johnny Bench (Catcher)");
    }

    /// <summary>A markdown table, alignment row and all, pasted in unchanged.</summary>
    [Fact]
    public void a_markdown_table_pasted_in()
    {
        _roster.TheRosterIs("""
            | player     | position |
            |------------|----------|
            | Nolan Ryan | Pitcher  |
            """);

        _roster.TheRosterReads("Nolan Ryan (Pitcher)");
    }

    /// <summary>
    /// Storyteller's <c>CreateNewObject&lt;T&gt;</c>: a table of records as the test input, with a
    /// relative date and the <c>Years</c> column left out because the record defaults it.
    /// </summary>
    [Fact]
    public void a_table_of_objects_as_the_input()
    {
        _roster.TheSigningsAre("""
            | Player       | Position | StartsOn |
            | Nolan Ryan   | Pitcher  | TODAY    |
            | Johnny Bench | Catcher  | TODAY+30 |
            """);

        _roster.TheSigningsRead("Nolan Ryan/Pitcher/1y, Johnny Bench/Catcher/1y");
    }

    /// <summary>
    /// StoryTeller.Samples/Specs/Tables/Tables.md — a decision table with one wrong answer, so the
    /// grid has a verdict per row and one of them disagrees.
    /// </summary>
    [Fact]
    public void a_decision_table_with_a_wrong_answer()
    {
        _roster.AddingNumbersTogether("""
            | x | y | sum |
            | 1 | 1 | 2   |
            | 3 | 4 | 7   |
            | 2 | 2 | 5   |
            """);
    }
}
