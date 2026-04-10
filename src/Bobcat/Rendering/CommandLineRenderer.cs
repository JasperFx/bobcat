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

        if (step.StepStatus == ResultStatus.failed)
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
            AnsiConsole.MarkupLine($"        {cellIcon} {Markup.Escape(cell.Name)}: {Markup.Escape(cell.DisplayText)}");
        }
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
