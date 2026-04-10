using Bobcat.Engine;
using JasperFx.Core;
using Spectre.Console;

namespace Bobcat.Rendering;

public class CommandLineRenderer
{
    public void RenderResults(string specTitle, ExecutionResults results)
    {
        var succeeded = results.Counts.Succeeded;
        var statusIcon = succeeded ? "[green]OK[/]" : "[red]FAILED[/]";

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  {Markup.Escape(specTitle)} {statusIcon}");
        AnsiConsole.MarkupLine($"  [dim]{new string('─', Math.Min(specTitle.Length + 10, 60))}[/]");

        foreach (var step in results.Steps)
        {
            RenderStepResult(step);
        }

        AnsiConsole.WriteLine();
        RenderCounts(results.Counts);

        if (results.Steps.Any(s => s.End > 0))
        {
            var totalMs = results.Steps.Where(s => s.End > 0).Max(s => s.End);
            AnsiConsole.MarkupLine($"  [dim]Duration: {totalMs}ms[/]");
        }

        AnsiConsole.WriteLine();
    }

    public void RenderStepResult(StepResult step)
    {
        var icon = step.StepStatus switch
        {
            ResultStatus.success => "[green]✓[/]",
            ResultStatus.failed => "[red]✗[/]",
            ResultStatus.error => "[yellow]![/]",
            ResultStatus.ok => "[dim]○[/]",
            _ => "[dim]?[/]"
        };

        var kindLabel = step.StepKind switch
        {
            StepKind.Given => "[dim]Given[/] ",
            StepKind.When => "[dim]When[/]  ",
            StepKind.Then => "[dim]Then[/]  ",
            StepKind.SetUp => "[dim]Setup[/] ",
            StepKind.TearDown => "[dim]Teardown[/] ",
            _ => ""
        };

        var duration = step.End > step.Start ? $" [dim]({step.End - step.Start}ms)[/]" : "";

        AnsiConsole.MarkupLine($"    {icon} {kindLabel}{Markup.Escape(step.StepId)}{duration}");

        if (step.StepStatus == ResultStatus.error && step.Exception != null)
        {
            AnsiConsole.MarkupLine($"      [yellow]{Markup.Escape(step.Exception.GetType().Name)}: {Markup.Escape(step.Exception.Message)}[/]");
        }

        if (step.StepStatus == ResultStatus.failed && !step.IsSetVerification)
        {
            AnsiConsole.MarkupLine($"      [red]Assertion failed[/]");
        }

        if (step.IsSetVerification && step.Cells.Count > 0)
        {
            RenderSetVerificationTable(step);
        }
        else
        {
            foreach (var cell in step.Cells)
            {
                var cellIcon = cell.Status switch
                {
                    ResultStatus.success => "[green]✓[/]",
                    ResultStatus.failed => "[red]✗[/]",
                    ResultStatus.error => "[yellow]![/]",
                    _ => " "
                };
                AnsiConsole.MarkupLine(
                    $"        {cellIcon} {Markup.Escape(cell.Name)}: {Markup.Escape(cell.DisplayText)}");
            }
        }
    }

    public void RenderSetVerificationTable(StepResult step)
    {
        var columns = step.SetVerificationColumns ?? [];
        if (columns.Count == 0)
        {
            // No structured columns — render cells as flat list
            foreach (var cell in step.Cells)
            {
                var icon = cell.Status switch
                {
                    ResultStatus.missing => "[red]✗[/]",
                    ResultStatus.invalid => "[yellow]?[/]",
                    _ => " "
                };
                AnsiConsole.MarkupLine($"        {icon} {Markup.Escape(cell.DisplayText)}");
            }
            return;
        }

        var table = new Spectre.Console.Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("[dim]#[/]").Centered());
        foreach (var col in columns)
        {
            table.AddColumn(new TableColumn(Markup.Escape(col)));
        }
        table.AddColumn(new TableColumn("[dim]Status[/]").Centered());

        // Group cells by RowIndex
        var rows = step.Cells.GroupBy(c => c.RowIndex).OrderBy(g => g.Key);
        foreach (var row in rows)
        {
            var rowCells = row.ToList();

            // Check for special row types
            var missingCell = rowCells.FirstOrDefault(c => c.Name == "missing-row");
            if (missingCell != null)
            {
                var emptyCols = columns.Select(_ => "[red]-[/]").ToList();
                emptyCols.Insert(0, $"[dim]{row.Key + 1}[/]");
                emptyCols.Add("[red]MISSING[/]");
                table.AddRow(emptyCols.ToArray());
                continue;
            }

            var extraCell = rowCells.FirstOrDefault(c => c.Name == "extra-row");
            if (extraCell != null)
            {
                var emptyCols = columns.Select(_ => "[yellow]...[/]").ToList();
                emptyCols.Insert(0, $"[dim]{row.Key + 1}[/]");
                emptyCols.Add("[yellow]EXTRA[/]");
                table.AddRow(emptyCols.ToArray());
                continue;
            }

            // Normal matched/compared row
            var values = new List<string> { $"[dim]{row.Key + 1}[/]" };
            var allOk = true;
            foreach (var col in columns)
            {
                var cell = rowCells.FirstOrDefault(c => c.Name == col);
                if (cell == null)
                {
                    values.Add("[dim]-[/]");
                    continue;
                }

                values.Add(cell.Status switch
                {
                    ResultStatus.success => $"[green]{Markup.Escape(cell.DisplayText)}[/]",
                    ResultStatus.failed => $"[red]{Markup.Escape(cell.DisplayText)}[/]",
                    _ => Markup.Escape(cell.DisplayText)
                });

                if (cell.Status != ResultStatus.success) allOk = false;
            }

            values.Add(allOk ? "[green]OK[/]" : "[red]FAIL[/]");
            table.AddRow(values.ToArray());
        }

        AnsiConsole.Write(table);
    }

    public void RenderCounts(Counts counts)
    {
        var color = counts.Succeeded ? "green" : "red";
        AnsiConsole.MarkupLine(
            $"  [{color}]{counts}[/]");
    }

    public void Render(Line line)
    {
        AnsiConsole.MarkupLine(line.Cells.Select(ToMarkup).Join(""));
    }

    public static string ToMarkup(Cell cell)
    {
        return cell.Mode switch
        {
            Mode.Text => Markup.Escape(cell.Text),
            Mode.Input => $"[italic]{Markup.Escape(cell.Text)}[/]",
            Mode.Right => $"[green italic]{Markup.Escape(cell.Text)}[/]",
            Mode.Error => $"[yellow italic]{Markup.Escape(cell.Text)}[/]",
            Mode.Wrong => $"[red italic]{Markup.Escape(cell.Text)}[/]",
            _ => Markup.Escape(cell.Text)
        };
    }
}
