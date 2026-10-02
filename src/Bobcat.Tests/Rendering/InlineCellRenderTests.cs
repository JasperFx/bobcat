using Bobcat.Engine;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Tests.Rendering;

/// <summary>
/// A comparison renders where its value sits in the sentence, the way Storyteller rendered a
/// sentence's cells — not on a line underneath.
/// </summary>
public class InlineCellRenderTests
{
    private const string Text = "For X=4 and Y=4, the Sum should be 6 and the Product should be 8";

    private static StepRender step(params CellRender[] cells) => new()
    {
        StepText = Text,
        Keyword = "Then",
        Status = ResultStatus.failed,
        Cells = cells.ToList(),
        ValueSpans =
        [
            new StepTextSpan(Text.IndexOf("4 and Y", StringComparison.Ordinal), 1, "x"),
            new StepTextSpan(Text.IndexOf("4,", StringComparison.Ordinal), 1, "y"),
            new StepTextSpan(Text.IndexOf("be 6", StringComparison.Ordinal) + 3, 1, "sum"),
            new StepTextSpan(Text.LastIndexOf('8'), 1, "product")
        ]
    };

    private static CellRender cell(string name, ResultStatus status, string display)
        => new() { Name = name, Status = status, DisplayText = display };

    [Fact]
    public void a_passing_comparison_turns_its_own_value_green()
    {
        var markup = CommandLineRenderer.Sentence(step(cell("Sum", ResultStatus.success, "6")));

        // The value the specification wrote, coloured — it is what happened, so there is nothing to
        // add underneath.
        markup.ShouldContain("should be [green]6[/]");
    }

    [Fact]
    public void a_failing_comparison_takes_the_place_of_the_value()
    {
        var markup = CommandLineRenderer.Sentence(
            step(cell("Sum", ResultStatus.failed, "expected '6', got '8'")));

        // The correction reads as the correction to that exact word.
        markup.ShouldContain("the Sum should be [red]expected '6', got '8'[/]");
    }

    [Fact]
    public void two_comparisons_in_one_sentence_are_each_judged_where_they_sit()
    {
        var markup = CommandLineRenderer.Sentence(step(
            cell("Sum", ResultStatus.failed, "expected '6', got '8'"),
            cell("Product", ResultStatus.failed, "expected '8', got '16'")));

        markup.ShouldContain("the Sum should be [red]expected '6', got '8'[/]");
        markup.ShouldContain("the Product should be [red]expected '8', got '16'[/]");
    }

    [Fact]
    public void an_input_value_with_no_comparison_stays_italic()
    {
        // X and Y are the step's data, not claims about it. Storyteller set input cells apart from
        // prose and this keeps doing that.
        var markup = CommandLineRenderer.Sentence(step(cell("Sum", ResultStatus.success, "6")));

        markup.ShouldContain("For X=[italic]4[/] and Y=[italic]4[/]");
    }

    [Fact]
    public void a_cell_rendered_inline_needs_no_line_of_its_own()
    {
        var rendered = step(cell("Sum", ResultStatus.success, "6"), cell("Product", ResultStatus.success, "8"));

        CommandLineRenderer.CellsRenderedInline(rendered).ShouldBe(new[] { "Sum", "Product" }, ignoreOrder: true);
    }

    [Fact]
    public void a_cell_matching_no_placeholder_keeps_its_line()
    {
        // The join is the placeholder name, so a cell with nowhere to go still has to report
        // somewhere — dropping it would lose the only place the value appears.
        var rendered = step(cell("Elapsed", ResultStatus.failed, "expected '1', got '9'"));

        CommandLineRenderer.CellsRenderedInline(rendered).ShouldBeEmpty();
        CommandLineRenderer.Sentence(rendered).ShouldNotContain("got '9'");
    }

    [Fact]
    public void a_lane_that_records_no_spans_keeps_every_line()
    {
        var rendered = new StepRender
        {
            StepText = Text,
            Cells = [cell("Sum", ResultStatus.failed, "expected '6', got '8'")]
        };

        CommandLineRenderer.CellsRenderedInline(rendered).ShouldBeEmpty();
        CommandLineRenderer.Sentence(rendered).ShouldBe(Text);
    }

    [Fact]
    public void a_grids_cells_never_compete_for_a_place_in_the_sentence()
    {
        // A grid cell belongs to a row and is drawn in the table. Only a scalar comparison — one the
        // step itself made — can take a word's place.
        var rendered = new StepRender
        {
            StepText = Text,
            ValueSpans = [new StepTextSpan(Text.IndexOf("be 6", StringComparison.Ordinal) + 3, 1, "sum")],
            Cells = [new CellRender
            {
                Name = "sum", Status = ResultStatus.failed, DisplayText = "expected '6', got '8'", RowIndex = 0
            }]
        };

        CommandLineRenderer.CellsRenderedInline(rendered).ShouldBeEmpty();
    }
}
