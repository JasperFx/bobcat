using Bobcat.Engine;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// A grammar taking a <see cref="StepTable"/>, driven from C# with a table literal. The same body a
/// <c>.feature</c> file binds to, so the two lanes cannot render one table two ways.
/// </summary>
public class ProjectedTableGrammar
{
    public readonly List<string> Seen = new();

    [Given("the roster is")]
    internal void TheRosterIs(StepTable roster)
    {
        foreach (var row in roster.AsDictionaries()) Seen.Add(row["player"]);
    }

    [Then("the sums are")]
    internal void TheSumsAre(StepTable sums)
    {
        var rows = sums.AsDictionaries();
        for (var i = 0; i < rows.Count; i++)
        {
            SpecAssert.Check("sum",
                int.Parse(rows[i]["x"]) + int.Parse(rows[i]["y"]),
                int.Parse(rows[i]["sum"]),
                rowIndex: i);
        }
    }
}

public class ProjectedTableTests
{
    private static SpecRender render(Action<ProjectedTableGrammar> body)
    {
        var grammar = new ProjectedTableGrammar();
        using var recording = ScenarioRecorder.Begin("Projected Tables", "a table literal", null, Guid.Empty);
        body(grammar);
        return SpecRender.FromRecording(recording);
    }

    [Fact]
    public void a_table_literal_reaches_the_grammar()
    {
        ProjectedTableGrammar? captured = null;

        render(g =>
        {
            captured = g;
            g.TheRosterIs("""
                | player     |
                | Nolan Ryan |
                | Willy Mays |
                """);
        });

        captured!.Seen.ShouldBe(new[] { "Nolan Ryan", "Willy Mays" });
    }

    [Fact]
    public void the_table_renders_as_a_grid_and_not_as_words_in_the_sentence()
    {
        var spec = render(g => g.TheRosterIs("""
            | player     |
            | Nolan Ryan |
            | Willy Mays |
            """));

        var step = spec.Steps.Single(s => s.StepText.Contains("the roster is"));

        // The sentence is prose; the data is a grid under it.
        step.StepText.ShouldBe("the roster is");
        var grid = step.SetVerification.ShouldNotBeNull();
        grid.Columns.ShouldBe(new[] { "player" });
        grid.Rows.Select(r => r.Cells.Single().DisplayText).ShouldBe(new[] { "Nolan Ryan", "Willy Mays" });
    }

    [Fact]
    public void an_input_table_does_not_cost_the_step_its_own_verdict()
    {
        var spec = render(g => g.TheRosterIs("""
            | player     |
            | Nolan Ryan |
            """));

        // The table's cells carry no verdict of their own, so the step still counts as the one thing
        // it did. Counting only cells read as zero rights and lost the step from the figures.
        spec.Counts.Rights.ShouldBe(1);
        spec.Counts.Wrongs.ShouldBe(0);
    }

    [Fact]
    public void a_decision_table_from_a_literal_reports_a_verdict_per_row()
    {
        var spec = render(g => g.TheSumsAre("""
            | x | y | sum |
            | 1 | 1 | 2   |
            | 2 | 2 | 5   |
            """));

        var grid = spec.Steps.Single(s => s.StepText.Contains("the sums are")).SetVerification.ShouldNotBeNull();

        grid.Rows[0].AllCellsOk.ShouldBeTrue();
        grid.Rows[1].AllCellsOk.ShouldBeFalse();

        // The grammar's own comparison supersedes the value the literal wrote for that column.
        grid.Rows[1].Cells.Single(c => c.Column == "sum").DisplayText.ShouldBe("expected '5', got '4'");

        // ...and the input columns are still the inputs.
        grid.Rows[1].Cells.Single(c => c.Column == "x").DisplayText.ShouldBe("2");
    }
}
