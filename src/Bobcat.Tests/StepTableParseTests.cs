using Bobcat;
using Shouldly;

namespace Bobcat.Tests;

/// <summary>
/// A table written as pipe-delimited text — how a projected test supplies the table a
/// <c>.feature</c> file would write in its trailing <c>|...|</c> block.
/// </summary>
public class StepTableParseTests
{
    [Fact]
    public void reads_headers_and_rows()
    {
        var table = StepTable.Parse("""
            | first  | last   |
            | LeBron | James  |
            | Chris  | Paul   |
            """);

        table.Headers.ShouldBe(new[] { "first", "last" });
        table.Rows.Count.ShouldBe(2);
        table.Rows[0].ShouldBe(new[] { "LeBron", "James" });
        table.Cell(1, "last").ShouldBe("Paul");
    }

    [Fact]
    public void a_markdown_alignment_row_is_punctuation_not_data()
    {
        var table = StepTable.Parse("""
            | first  | last  |
            |--------|:-----:|
            | LeBron | James |
            """);

        table.Rows.Count.ShouldBe(1);
        table.Rows[0].ShouldBe(new[] { "LeBron", "James" });
    }

    [Fact]
    public void an_alignment_row_anywhere_else_is_data_because_it_could_be_a_value()
    {
        var table = StepTable.Parse("""
            | label | value |
            | a     | 1     |
            | ---   | ---   |
            """);

        table.Rows.Count.ShouldBe(2);
        table.Rows[1].ShouldBe(new[] { "---", "---" });
    }

    [Fact]
    public void the_outer_pipes_are_optional()
    {
        StepTable.Parse("""
            first | last
            LeBron | James
            """).Rows[0].ShouldBe(new[] { "LeBron", "James" });
    }

    [Fact]
    public void cells_are_trimmed_and_blank_lines_ignored()
    {
        var table = StepTable.Parse("\n|  a  |   b |\n\n|  1  | 2   |\n\n");

        table.Headers.ShouldBe(new[] { "a", "b" });
        table.Rows[0].ShouldBe(new[] { "1", "2" });
    }

    [Fact]
    public void an_empty_cell_stays_an_empty_cell()
    {
        StepTable.Parse("""
            | a | b |
            | 1 |   |
            """).Cell(0, "b").ShouldBe("");
    }

    [Fact]
    public void a_header_row_on_its_own_is_a_table_with_no_rows()
    {
        var table = StepTable.Parse("| a | b |");

        table.Headers.ShouldBe(new[] { "a", "b" });
        table.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void text_with_no_rows_at_all_says_what_was_expected()
    {
        var ex = Should.Throw<ArgumentException>(() => StepTable.Parse("   \n  \n"));

        ex.Message.ShouldContain("| first | last |");
    }

    [Fact]
    public void parse_is_the_inverse_of_ToString()
    {
        var original = new StepTable(
            new[] { "first", "last" },
            new IReadOnlyList<string>[] { new[] { "LeBron", "James" }, new[] { "Chris", "Paul" } });

        var round = StepTable.Parse(original.ToString());

        round.Headers.ShouldBe(original.Headers);
        round.ToString().ShouldBe(original.ToString());
    }

    [Fact]
    public void a_string_reads_as_a_table_so_a_call_site_can_pass_the_text_itself()
    {
        StepTable table = """
            | first  |
            | LeBron |
            """;

        table.Headers.ShouldBe(new[] { "first" });
    }
}
