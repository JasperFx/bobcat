using Bobcat.Engine;
using Bobcat.Resilience;
using Bobcat.Runtime;
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

        if (step.Status is (ResultStatus.error or ResultStatus.failed) && step.ErrorMessage != null)
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

        // Render correlated logs
        if (step.Logs.Count > 0)
        {
            AnsiConsole.MarkupLine("      [dim]Logs:[/]");
            foreach (var log in step.Logs)
            {
                AnsiConsole.MarkupLine($"      [dim]  {Markup.Escape(log)}[/]");
            }
        }

        // Render diagnostics
        if (step.Diagnostics.Count > 0)
        {
            AnsiConsole.MarkupLine("      [dim]Diagnostics:[/]");
            foreach (var (key, value) in step.Diagnostics)
            {
                AnsiConsole.MarkupLine($"      [dim]  {Markup.Escape(key)}: {Markup.Escape(value)}[/]");
            }
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

    /// <summary>
    /// A harness failure as it happens — a resource that would not start, a feature hook that
    /// threw. Rendered where a feature header would have been, so the console shows the reason
    /// at the point the run stopped rather than only in the summary.
    /// </summary>
    public void RenderCatastrophicFailure(string description)
    {
        AnsiConsole.MarkupLine($"  [red bold]✗ {Markup.Escape(description)}[/]");
    }

    /// <summary>
    /// The harness section of the summary: what broke, and every scenario that did not run
    /// because of it. Silent when the harness held up.
    /// </summary>
    public void RenderHarnessSummary(SuiteResults results)
    {
        if (results.PreflightFailure is null && results.CatastrophicFailure is null &&
            results.NotRun.Count == 0 && results.Features.All(f => f.LifecycleFailure is null))
        {
            return;
        }

        AnsiConsole.WriteLine();

        if (results.PreflightFailure is not null)
        {
            AnsiConsole.MarkupLine($"  [red bold]{Markup.Escape(results.PreflightFailure)}[/]");
        }

        if (results.CatastrophicFailure is not null)
        {
            AnsiConsole.MarkupLine($"  [red bold]Catastrophic: {Markup.Escape(results.CatastrophicFailure)}[/]");
        }

        foreach (var feature in results.Features.Where(f => f.LifecycleFailure is not null))
        {
            AnsiConsole.MarkupLine($"  [red]{Markup.Escape(feature.LifecycleFailure!)}[/]");
        }

        if (results.NotRun.Count > 0)
        {
            AnsiConsole.MarkupLine($"  [red]{results.NotRun.Count} scenario(s) did not run[/]");
            foreach (var scenario in results.NotRun)
            {
                AnsiConsole.MarkupLine(
                    $"    [red]•[/] {Markup.Escape(scenario.FeatureTitle)}: {Markup.Escape(scenario.Title)}");
            }
        }
    }

    public void RenderCounts(Counts counts)
    {
        var color = counts.Succeeded ? "green" : "red";
        AnsiConsole.MarkupLine($"  [{color}]{counts}[/]");
    }

    // --- Retry reporting ---
    //
    // Retries are shown as they happen and again on the scenario's own line. A retry that only
    // appears in the final summary reads as a clean pass while the run is in flight, which is
    // exactly the laundering this feature has to avoid.

    /// <summary>Announces a retry before the next attempt starts.</summary>
    public void RenderRetryNotice(string scenarioTitle, int nextAttempt, string reason)
    {
        AnsiConsole.MarkupLine(
            $"  [yellow]↻ retrying[/] [italic]{Markup.Escape(scenarioTitle)}[/] " +
            $"[grey](attempt {nextAttempt}: {Markup.Escape(reason)})[/]");
    }

    /// <summary>
    /// Marks a scenario that needed more than one attempt, or whose requested retry could not
    /// be honoured. Silent for the ordinary clean-pass case.
    /// </summary>
    public void RenderRetrySummary(ScenarioResult result)
    {
        if (result.Outcome == RunOutcome.PassOnRetry)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]⚠ passed on retry[/] [grey]after {result.AttemptCount} attempts — " +
                "not a clean pass[/]");
        }

        // A hint that stopped a tagged scenario from retrying is the case most in need of saying
        // so out loud: nothing else on screen would explain why the tag appeared not to work.
        if (result.Attempts.LastOrDefault() is { Disposition: { Hint: { } hint, IsRetry: false } })
        {
            AnsiConsole.MarkupLine(
                $"  [grey]↯ recovery hint applied:[/] [italic]{Markup.Escape(hint.ToString())}[/]");
        }

        foreach (var unsupported in result.UnsupportedDispositions)
        {
            AnsiConsole.MarkupLine($"  [yellow]⚠ {Markup.Escape(unsupported)}[/]");
        }
    }

    /// <summary>The run-level flakiness ledger. Silent when everything passed cleanly.</summary>
    public void RenderResilienceSummary(SuiteResults results)
    {
        var passedOnRetry = results.PassedOnRetry;
        if (passedOnRetry.Count == 0 && results.UnsupportedDispositions.Count == 0) return;

        AnsiConsole.WriteLine();

        if (passedOnRetry.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]{passedOnRetry.Count} scenario(s) passed on retry[/] " +
                $"[grey]({results.RetriesPerformed} retries performed)[/]");

            foreach (var scenario in passedOnRetry)
            {
                AnsiConsole.MarkupLine(
                    $"    [yellow]•[/] {Markup.Escape(scenario.Title)} " +
                    $"[grey]({scenario.AttemptCount} attempts)[/]");
            }
        }

        foreach (var unsupported in results.UnsupportedDispositions)
        {
            AnsiConsole.MarkupLine($"  [yellow]⚠ {Markup.Escape(unsupported)}[/]");
        }
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
