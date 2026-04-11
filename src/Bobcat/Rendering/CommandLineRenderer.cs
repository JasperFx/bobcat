using Bobcat.Engine;
using JasperFx.Core;
using Spectre.Console;

namespace Bobcat.Rendering;

public class CommandLineRenderer
{
    public void RenderFeatureHeader(string featureTitle)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Feature: {Markup.Escape(featureTitle)}[/]");
        AnsiConsole.MarkupLine($"[dim]{new string('═', Math.Min(featureTitle.Length + 10, 60))}[/]");
    }

    // --- SpecRender-based rendering (primary) ---

    public void Render(SpecRender spec)
    {
        var statusIcon = spec.Succeeded ? "[green]OK[/]" : "[red]FAILED[/]";

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  {Markup.Escape(spec.Title)} {statusIcon}");
        AnsiConsole.MarkupLine($"  [dim]{new string('─', Math.Min(spec.Title.Length + 10, 60))}[/]");

        foreach (var step in spec.Steps)
        {
            RenderStep(step);
        }

        AnsiConsole.WriteLine();
        RenderCounts(spec.Counts);

        if (spec.DurationMs > 0)
        {
            AnsiConsole.MarkupLine($"  [dim]Duration: {spec.DurationMs}ms[/]");
        }

        AnsiConsole.WriteLine();
    }

    public void RenderStep(StepRender step)
    {
        var icon = step.Status switch
        {
            ResultStatus.success => "[green]✓[/]",
            ResultStatus.failed => "[red]✗[/]",
            ResultStatus.error => "[yellow]![/]",
            ResultStatus.ok => "[dim]○[/]",
            _ => "[dim]?[/]"
        };

        var kindLabel = step.Kind switch
        {
            StepKind.Given => "[dim]Given[/] ",
            StepKind.When => "[dim]When[/]  ",
            StepKind.Then => "[dim]Then[/]  ",
            StepKind.SetUp => "[dim]Setup[/] ",
            StepKind.TearDown => "[dim]Teardown[/] ",
            _ => ""
        };

        var duration = step.DurationMs > 0 ? $" [dim]({step.DurationMs}ms)[/]" : "";

        AnsiConsole.MarkupLine($"    {icon} {kindLabel}{Markup.Escape(step.StepText)}{duration}");

        if (step.Status == ResultStatus.error && step.ErrorMessage != null)
        {
            var exType = step.ExceptionType != null ? $"{Markup.Escape(step.ExceptionType)}: " : "";
            AnsiConsole.MarkupLine($"      [yellow]{exType}{Markup.Escape(step.ErrorMessage)}[/]");
        }

        if (step.SetVerification != null)
        {
            RenderSetVerification(step.SetVerification);
        }
        else if (step.Status == ResultStatus.failed && step.SetVerification == null)
        {
            AnsiConsole.MarkupLine($"      [red]Assertion failed[/]");
        }

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

    public void RenderSetVerification(SetVerificationRender sv)
    {
        if (sv.Columns.Count == 0) return;

        var table = new Spectre.Console.Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("[dim]#[/]").Centered());
        foreach (var col in sv.Columns)
        {
            table.AddColumn(new TableColumn(Markup.Escape(col)));
        }
        table.AddColumn(new TableColumn("[dim]Status[/]").Centered());

        var rowNum = 0;
        foreach (var row in sv.Rows)
        {
            rowNum++;
            switch (row.RowType)
            {
                case SetVerificationRowType.Missing:
                {
                    var cols = sv.Columns.Select(_ => "[red]-[/]").ToList();
                    cols.Insert(0, $"[dim]{rowNum}[/]");
                    cols.Add("[red]MISSING[/]");
                    table.AddRow(cols.ToArray());
                    break;
                }
                case SetVerificationRowType.Extra:
                {
                    var cols = new List<string> { $"[dim]{rowNum}[/]" };
                    if (row.Cells.Count > 0)
                    {
                        foreach (var cell in row.Cells)
                        {
                            cols.Add($"[yellow]{Markup.Escape(cell.DisplayText)}[/]");
                        }
                    }
                    else
                    {
                        cols.AddRange(sv.Columns.Select(_ => "[yellow]...[/]"));
                    }
                    cols.Add("[yellow]EXTRA[/]");
                    table.AddRow(cols.ToArray());
                    break;
                }
                default:
                {
                    var values = new List<string> { $"[dim]{rowNum}[/]" };
                    foreach (var cell in row.Cells)
                    {
                        values.Add(cell.Status switch
                        {
                            ResultStatus.success => $"[green]{Markup.Escape(cell.DisplayText)}[/]",
                            ResultStatus.failed => $"[red]{Markup.Escape(cell.DisplayText)}[/]",
                            _ => Markup.Escape(cell.DisplayText)
                        });
                    }
                    values.Add(row.AllCellsOk ? "[green]OK[/]" : "[red]FAIL[/]");
                    table.AddRow(values.ToArray());
                    break;
                }
            }
        }

        AnsiConsole.Write(table);
    }

    // --- Legacy ExecutionResults-based rendering (bridge) ---

    public void RenderResults(string specTitle, ExecutionResults results)
    {
        Render(SpecRender.FromResults(specTitle, results));
    }

    public void RenderCounts(Counts counts)
    {
        var color = counts.Succeeded ? "green" : "red";
        AnsiConsole.MarkupLine($"  [{color}]{counts}[/]");
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
