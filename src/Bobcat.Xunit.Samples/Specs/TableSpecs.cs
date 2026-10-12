using Bobcat.Xunit.Samples.Grammars;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// Storyteller's table samples from the C# side: the tabular data written as a table literal in the
/// test itself.
/// </summary>
[BobcatFeature("Tables"), BobcatScenario]
public class TableSpecs
{
    private readonly RosterGrammar _roster = new();

    /// <summary>
    /// Samples/Specs/Tables/Table_with_Options.md, with the `position` column present on two rows
    /// and left out of the third.
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

    /// <summary>
    /// A decision table whose outputs are out parameters plus a returned value: a judged column for
    /// each of <c>average</c>, <c>On Base</c> and <c>Verdict</c>.
    /// </summary>
    [Fact]
    public async Task a_decision_table_with_out_parameters()
    {
        await _roster.TheBattingLinesAre("""
            | At Bats | hits | walks | average | On Base | Verdict  |
            | 500     | 150  | 50    | 0.300   | 0.364   | All-Star |
            | 400     | 100  | 40    | 0.250   | 0.318   | Bench    |
            | 600     | 198  | 0     | 0.330   | 0.330   | All-Star |
            """);
    }
}
