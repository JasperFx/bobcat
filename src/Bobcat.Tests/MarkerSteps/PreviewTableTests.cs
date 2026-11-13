using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// bobcat#827: a planned step whose table was a constant at the call site previews as a grid under the
/// step — in the console, and in the outline a resident runner registers — read through the one parser
/// the run uses. A step with no table, or a table the run would refuse, previews as its sentence only.
/// </summary>
public class PreviewTableTests : IDisposable
{
    private const string Uid = "Preview tables 827/the underwriting rules";

    private const string Table =
        """
        | Credit Score | Decision | Rate |
        | 579          | Decline  | NULL |
        | 580          | Approve  | 9.75 |
        """;

    public void Dispose()
    {
        PlannedSteps.Remove(Uid);
        DeclaredSteps.Remove(Uid);
    }

    private static PreviewRender preview()
    {
        DeclaredSteps.Bind(Uid, "Specs.Underwriting", "the_underwriting_rules");
        return ProjectedSpecPreview.For(Uid);
    }

    [Fact]
    public void a_constant_table_is_parsed_onto_the_previewed_step_with_the_runs_own_parser()
    {
        PlannedSteps.Register(Uid,
            new PlannedStep("Then", "the underwriting decisions are", "Rules.TheDecisionsAre", null, 12, Table));

        var step = preview().Steps.Single();

        var table = step.Table.ShouldNotBeNull();
        table.Headers.ShouldBe(["Credit Score", "Decision", "Rate"]);
        table.Rows.Count.ShouldBe(2);
        table.Rows[0].ShouldBe(["579", "Decline", "NULL"]);
        table.ToString().ShouldBe(StepTable.Parse(Table).ToString());
    }

    [Fact]
    public void a_step_with_no_table_previews_as_its_sentence_only()
    {
        PlannedSteps.Register(Uid,
            new PlannedStep("Then", "the total is {total}", "Rules.TheTotalIs", null, 12));

        var step = preview().Steps.Single();

        step.Table.ShouldBeNull();
        step.TableGrid.ShouldBeNull();
    }

    [Fact]
    public void a_literal_the_run_would_refuse_is_left_off_rather_than_failing_the_preview()
    {
        PlannedSteps.Register(Uid,
            new PlannedStep("Then", "the decisions are", "Rules.TheDecisionsAre", null, 12, "   "));

        preview().Steps.Single().Table.ShouldBeNull();
    }

    [Fact]
    public void the_grid_is_unjudged_so_it_shows_no_verdict_column()
    {
        var grid = SetVerificationRender.FromTable(StepTable.Parse(Table));

        grid.Columns.ShouldBe(["Credit Score", "Decision", "Rate"]);
        grid.Rows.Select(r => r.RowType).ShouldAllBe(t => t == SetVerificationRowType.Matched);
        grid.Rows[1].Cells.Select(c => c.DisplayText).ShouldBe(["580", "Approve", "9.75"]);
        grid.Rows.SelectMany(r => r.Cells).Select(c => c.Status).ShouldAllBe(s => s == ResultStatus.ok);
        CommandLineRenderer.ShowsStatusColumn(grid).ShouldBeFalse();
    }

    [Fact]
    public void the_console_preview_draws_the_grid_under_the_step()
    {
        PlannedSteps.Register(Uid,
            new PlannedStep("Then", "the underwriting decisions are", "Rules.TheDecisionsAre", null, 12, Table));

        var writer = new StringWriter();
        var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Ansi = Spectre.Console.AnsiSupport.No,
            ColorSystem = Spectre.Console.ColorSystemSupport.NoColors,
            Out = new Spectre.Console.AnsiConsoleOutput(writer)
        });
        console.Profile.Width = 200;

        new CommandLineRenderer(console).RenderPreview(preview());
        var text = writer.ToString();

        var sentence = text.IndexOf("the underwriting decisions are", StringComparison.Ordinal);
        var binding = text.IndexOf("Rules.TheDecisionsAre", StringComparison.Ordinal);
        var header = text.IndexOf("Credit Score", StringComparison.Ordinal);
        sentence.ShouldBeGreaterThanOrEqualTo(0);
        binding.ShouldBeGreaterThan(sentence);
        header.ShouldBeGreaterThan(binding);

        text.ShouldContain("Approve");
        text.ShouldContain("9.75");
        text.ShouldNotContain("Verdict");
    }

    [Fact]
    public void the_outline_carries_the_table_keyed_by_its_steps_index()
    {
        DeclaredSteps.Register(Uid, new DeclaredStep("Given", "an underwriting policy", 10));
        PlannedSteps.Register(Uid,
            new PlannedStep("Then", "the underwriting decisions are", "Rules.TheDecisionsAre", null, 12, Table));
        DeclaredSteps.Bind(Uid, "Specs.Underwriting", "the_underwriting_rules");

        var outline = SpecOutline.All().Single(x => x.Identity == Uid);

        outline.Steps.ShouldBe(["Given an underwriting policy", "Then the underwriting decisions are"]);
        var table = outline.Tables.ShouldNotBeNull().Single();
        table.Step.ShouldBe(1);
        table.Headers.ShouldBe(["Credit Score", "Decision", "Rate"]);
        table.Rows.Select(r => string.Join("|", r)).ShouldBe(["579|Decline|NULL", "580|Approve|9.75"]);
    }

    [Fact]
    public void an_outline_with_no_tables_leaves_the_member_null()
    {
        PlannedSteps.Register(Uid,
            new PlannedStep("Then", "the total is {total}", "Rules.TheTotalIs", null, 12));
        DeclaredSteps.Bind(Uid, "Specs.Underwriting", "the_underwriting_rules");

        SpecOutline.All().Single(x => x.Identity == Uid).Tables.ShouldBeNull();
    }
}
