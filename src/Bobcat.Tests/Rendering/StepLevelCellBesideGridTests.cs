using Bobcat.Engine;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Tests.Rendering;

/// <summary>
/// A step with a table can also record a cell that belongs to the step rather than to a row: the
/// act's own cell under <c>WhenPosted</c>, or a refusal's <c>status</c> check beside a ProblemDetails
/// grid (bobcat#617). Such a cell keeps the default <c>RowIndex</c> of -1.
/// </summary>
public class StepLevelCellBesideGridTests
{
    private static List<CellResult> refusalCells() =>
    [
        new CellResult("status", ResultStatus.success) { Expected = "4xx", Actual = "400" },
        new CellResult("Detail", ResultStatus.success) { Expected = "Insufficient funds", Actual = "Insufficient funds", RowIndex = 0 }
    ];

    [Fact]
    public void a_step_level_cell_is_not_folded_into_the_grid_as_an_empty_row()
    {
        var grid = SetVerificationRender.FromCells(["Detail"], refusalCells());

        var row = grid.Rows.ShouldHaveSingleItem();
        row.Cells.Single(c => c.Column == "Detail").DisplayText.ShouldNotBeEmpty();
    }

    [Fact]
    public void the_console_shows_the_grid_once_and_the_step_level_cell_beside_it()
    {
        var cells = refusalCells();
        var step = new StepRender
        {
            Keyword = "And",
            StepText = "refused with ProblemDetails(Detail: Insufficient funds)",
            Status = ResultStatus.success,
            Cells = cells.Select(CellRender.From).ToList(),
            SetVerification = SetVerificationRender.FromCells(["Detail"], cells)
        };

        var writer = new StringWriter();
        var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Ansi = Spectre.Console.AnsiSupport.No,
            ColorSystem = Spectre.Console.ColorSystemSupport.NoColors,
            Out = new Spectre.Console.AnsiConsoleOutput(writer)
        });
        console.Profile.Width = 200;
        new CommandLineRenderer(console).RenderStep(step);

        var lines = writer.ToString().Split('\n');

        // The row's cell is in the grid and is not listed again underneath it.
        lines.Count(l => l.Contains("Insufficient funds") && !l.Contains("refused with")).ShouldBe(1);

        // The step's own cell was in no row, so it is listed rather than lost.
        lines.ShouldContain(l => l.Contains("status:"));
    }
}
