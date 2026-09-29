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

        if (spec.Steps.Count == 0)
        {
            // A scenario with no steps is Bobcat's pending-specification hotspot everywhere else,
            // so it says so here rather than rendering as a blank that reads like a clean pass.
            AnsiConsole.MarkupLine("    [dim]○ this specification declares no steps[/]");
        }

        RenderExceptions(spec);

        if (spec.ScenarioFailure is { } failure)
        {
            AnsiConsole.WriteLine();
            foreach (var line in failure.Split('\n'))
            {
                AnsiConsole.MarkupLine($"    [red]{Markup.Escape(line.TrimEnd())}[/]");
            }
        }

        AnsiConsole.WriteLine();
        RenderCounts(spec.Counts, spec.Succeeded);

        if (spec.DurationMs > 0)
        {
            AnsiConsole.MarkupLine($"  [dim]Duration: {spec.DurationMs}ms[/]");
        }

        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// The exceptions a specification's steps ended in, at the bottom, formatted by Spectre.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>At the bottom, not on the step.</b> A stack trace is the longest thing in a specification
    /// report and the least useful part of scanning it: the reader wants to know WHICH step broke,
    /// and then, separately, why. Inline, one exception pushes the rest of the specification off the
    /// screen. Storyteller collected them into a block for exactly this reason.
    /// </para>
    /// <para>
    /// Only real throws appear. A gathered wrong carries a <c>SpecAssertionException</c> that was
    /// never thrown, and it has no stack — its message belongs on the step's own line, where it is.
    /// </para>
    /// <para>
    /// <b>Rendered here rather than through Spectre's own formatter</b> for one reason: the formatter
    /// cannot be told which frames to leave out, and three of the five frames in a projected step's
    /// stack are Bobcat's plumbing and the test runner's. <see cref="SpecStackTrace"/> is the rule,
    /// and the number of frames it removed is always reported.
    /// </para>
    /// </remarks>
    public void RenderExceptions(SpecRender spec)
    {
        var errored = spec.Steps.Where(x => x.Exception is not null).ToList();
        if (errored.Count == 0) return;

        foreach (var step in errored)
        {
            var exception = step.Exception!;
            var filtered = SpecStackTrace.Filter(exception);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"    [yellow]{Markup.Escape(step.StepText)}[/]");
            AnsiConsole.MarkupLine(
                $"    [red]{Markup.Escape(exception.GetType().Name)}[/]: {Markup.Escape(exception.Message)}");

            foreach (var frame in filtered.Frames)
            {
                AnsiConsole.MarkupLine($"      [dim]{Markup.Escape(SpecStackTrace.Shorten(frame))}[/]");
            }

            if (filtered.Hidden > 0)
            {
                // Said out loud. A stack that was quietly edited is a stack a reader cannot trust.
                AnsiConsole.MarkupLine($"      [dim]({filtered.Hidden} framework frames hidden)[/]");
            }

            // An inner exception is usually the real story — a handler wrapping a validation failure,
            // a Task wrapping what actually threw — and Spectre's own formatter is the thing that
            // used to show it. Its stack gets the same treatment.
            for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            {
                AnsiConsole.MarkupLine(
                    $"    [dim]---[/] [red]{Markup.Escape(inner.GetType().Name)}[/]: {Markup.Escape(inner.Message)}");

                var innerFrames = SpecStackTrace.Filter(inner);
                foreach (var frame in innerFrames.Frames)
                {
                    AnsiConsole.MarkupLine($"      [dim]{Markup.Escape(SpecStackTrace.Shorten(frame))}[/]");
                }
            }
        }
    }

    /// <summary>
    /// Renders a scenario preview (issue #208): the planned steps, each annotated with the
    /// fixture method it bound to and where every parameter's value comes from — the one thing
    /// reading the <c>.feature</c> file cannot show.
    /// </summary>
    public void RenderPreview(PreviewRender preview)
    {
        AnsiConsole.WriteLine();
        var tags = preview.Tags.Length > 0
            ? " [blue]" + Markup.Escape(string.Join(" ", preview.Tags.Select(t => $"@{t}"))) + "[/]"
            : "";
        AnsiConsole.MarkupLine($"  {Markup.Escape(preview.Title)}{tags}");
        AnsiConsole.MarkupLine($"  [dim]{new string('─', Math.Min(preview.Title.Length + 10, 60))}[/]");

        if (preview.Error != null)
        {
            AnsiConsole.MarkupLine($"    [red]✗ {Markup.Escape(preview.Error)}[/]");
            return;
        }

        foreach (var step in preview.Steps)
        {
            var kindLabel = step.Keyword switch
            {
                { Length: > 0 } keyword => $"[dim]{Markup.Escape(keyword.PadRight(5))}[/] ",
                "" => "",
                _ => step.Kind switch
                {
                    StepKind.Given => "[dim]Given[/] ",
                    StepKind.When => "[dim]When[/]  ",
                    StepKind.Then => "[dim]Then[/]  ",
                    StepKind.SetUp => "[dim]Setup[/] ",
                    StepKind.TearDown => "[dim]Teardown[/] ",
                    _ => ""
                }
            };

            var indent = step.IsNarrative ? "    " : "      ";
            AnsiConsole.MarkupLine($"{indent}[dim]○[/] {kindLabel}{Markup.Escape(step.StepText)}");

            if (step.IsNarrative)
            {
                // A marker comment is the narrative the steps below it sit under. It has no binding
                // and saying "(no binding metadata)" about prose would read as a problem.
                continue;
            }

            if (step.Binding == null)
            {
                // Code-first specs and hand-built definitions carry no generated metadata —
                // that is not an error, so say so quietly rather than implying a broken match.
                AnsiConsole.MarkupLine($"{indent}  [dim]↳ (no binding metadata)[/]");
                continue;
            }

            var binding = step.Binding;

            // The expression only earns a line when it differs from the step text. In the Gherkin
            // lane it always does — the text is the author's sentence and the expression is the
            // pattern it matched. In the projected lane the step text IS the template, and printing
            // it twice is the kind of noise that makes a tool look like it has nothing to say.
            var expression = binding.Expression == step.StepText
                ? ""
                : $" [dim]— \"{Markup.Escape(binding.Expression)}\"[/]";

            AnsiConsole.MarkupLine(
                $"{indent}  [dim]↳[/] [cyan]{Markup.Escape(binding.DeclaringTypeName)}.{Markup.Escape(binding.Method)}[/]"
                + expression);

            foreach (var argument in binding.Arguments)
            {
                var origin = argument.Source switch
                {
                    Runtime.StepArgumentSource.Capture => $"\"{Markup.Escape(argument.Value)}\" [dim](capture)[/]",
                    Runtime.StepArgumentSource.TableColumn => $"column [yellow]{Markup.Escape(argument.Value)}[/]",
                    Runtime.StepArgumentSource.DocString => "[dim]doc string[/]",
                    Runtime.StepArgumentSource.Table => "[dim]the step's table[/]",
                    Runtime.StepArgumentSource.Service => $"[green]{Markup.Escape(argument.Value)}[/] [dim](injected)[/]",
                    Runtime.StepArgumentSource.Expected => $"\"{Markup.Escape(argument.Value)}\" [dim](expected)[/]",
                    _ => "[dim]default[/]"
                };
                AnsiConsole.MarkupLine($"{indent}    {Markup.Escape(argument.Name)} [dim]←[/] {origin}");
            }
        }
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

        // The keyword as the specification wrote it wins over the kind, because `And` and `But`
        // are keywords with no kind: StepKind has no member for them, and rendering a continuation
        // as a second `Given` is a small lie the projected lane can avoid telling.
        //
        // An EMPTY keyword is a third state, and a deliberate one: a grammar may spell no keyword at
        // all, the way Storyteller and Gauge sentences read, and such a step gets no label rather
        // than falling back to a kind it never claimed.
        var kindLabel = step.Keyword switch
        {
            { Length: > 0 } keyword => $"[dim]{Markup.Escape(keyword.PadRight(5))}[/] ",
            "" => "",
            _ => step.Kind switch
            {
                StepKind.Given => "[dim]Given[/] ",
                StepKind.When => "[dim]When[/]  ",
                StepKind.Then => "[dim]Then[/]  ",
                StepKind.SetUp => "[dim]Setup[/] ",
                StepKind.TearDown => "[dim]Teardown[/] ",
                _ => ""
            }
        };

        var duration = step.DurationMs > 0 ? $" [dim]({step.DurationMs}ms)[/]" : "";
        var indent = new string(' ', 4 + step.Depth * 2);
        var sentence = Sentence(step);

        if (step.NotRun)
        {
            // Greyed out whole, Storyteller's rendering for a step the run never reached.
            AnsiConsole.MarkupLine($"{indent}[dim]{icon} {kindLabel}{sentence} — not run[/]");
            return;
        }

        AnsiConsole.MarkupLine($"{indent}{icon} {kindLabel}{sentence}{duration}");

        if (step.Status == ResultStatus.failed && step.ErrorMessage != null)
        {
            // A wrong says what it is, on the line, with no stack. Storyteller's whole argument for
            // StoryTellerAssert over an exception.
            foreach (var line in step.ErrorMessage.Split('\n'))
            {
                AnsiConsole.MarkupLine($"{indent}  [red]{Markup.Escape(line.TrimEnd())}[/]");
            }
        }
        else if (step.Status == ResultStatus.error && step.ErrorMessage != null)
        {
            // An ERROR gets one line here and its detail at the bottom of the specification — the
            // stack is the longest thing in the report and the least useful to the reader scanning
            // for which step broke. Storyteller collected exceptions in a block for the same reason.
            var exType = step.ExceptionType != null ? Markup.Escape(step.ExceptionType) : "exception";
            AnsiConsole.MarkupLine($"{indent}  [yellow]{exType} — see below[/]");
        }

        if (step.SetVerification != null)
        {
            RenderSetVerification(step.SetVerification);
        }

        // No "Assertion failed" line for a step with nothing else to show. The red ✗ already says the
        // step failed, and a bare Fact has nothing to add — that manufactured sentence is the noise
        // Storyteller's own StoryTellerAssert existed to replace.


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
                $"{indent}    {cellIcon} {Markup.Escape(cell.Name)}: {Markup.Escape(cell.DisplayText)}");
        }

        // Render correlated logs
        if (step.Logs.Count > 0)
        {
            AnsiConsole.MarkupLine($"{indent}  [dim]Logs:[/]");
            foreach (var log in step.Logs)
            {
                AnsiConsole.MarkupLine($"{indent}    [dim]{Markup.Escape(log)}[/]");
            }
        }

        // Render diagnostics
        if (step.Diagnostics.Count > 0)
        {
            AnsiConsole.MarkupLine($"{indent}  [dim]Diagnostics:[/]");
            foreach (var (key, value) in step.Diagnostics)
            {
                AnsiConsole.MarkupLine($"{indent}    [dim]{Markup.Escape(key)}: {Markup.Escape(value)}[/]");
            }
        }
    }

    /// <summary>
    /// A step's sentence as markup, with its input values in <b>italics</b>.
    /// </summary>
    /// <remarks>
    /// Storyteller set a sentence's input cells apart from its prose, and it earns its keep: a step
    /// reads as a sentence and the one thing anyone scans for is which parts of it were the data.
    /// The spans come from the substitution itself rather than from searching the finished text for
    /// the values, so a value that also occurs in the prose cannot mark the wrong run of characters.
    /// </remarks>
    public static string Sentence(StepRender step)
    {
        if (step.ValueSpans.Count == 0) return Markup.Escape(step.StepText);

        var markup = new System.Text.StringBuilder();
        var at = 0;

        foreach (var span in step.ValueSpans.OrderBy(x => x.Start))
        {
            if (span.Start < at || span.Start + span.Length > step.StepText.Length) continue;

            markup.Append(Markup.Escape(step.StepText[at..span.Start]));
            markup.Append("[italic]")
                .Append(Markup.Escape(step.StepText.Substring(span.Start, span.Length)))
                .Append("[/]");

            at = span.Start + span.Length;
        }

        markup.Append(Markup.Escape(step.StepText[at..]));
        return markup.ToString();
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
        if (results.DiscoveryFailure is null && results.PreflightFailure is null &&
            results.CatastrophicFailure is null &&
            results.NotRun.Count == 0 && results.Features.All(f => f.LifecycleFailure is null))
        {
            return;
        }

        AnsiConsole.WriteLine();

        if (results.DiscoveryFailure is not null)
        {
            AnsiConsole.MarkupLine($"  [red bold]{Markup.Escape(results.DiscoveryFailure)}[/]");
        }

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

    public void RenderCounts(Counts counts) => RenderCounts(counts, counts.Succeeded);

    /// <summary>
    /// The counts, with the verdict word supplied by the caller rather than derived from the
    /// figures.
    /// </summary>
    /// <remarks>
    /// A specification can fail with every count at zero — an assertion library threw between steps,
    /// or nothing got far enough to make a claim — and <c>Counts.Succeeded</c> reads "no wrongs, no
    /// errors" and says <i>Succeeded</i>. Printing that in green under a red heading is the one
    /// contradiction a report must never contain, so the outcome is passed in by whoever knows it.
    /// </remarks>
    public void RenderCounts(Counts counts, bool succeeded)
    {
        var color = succeeded ? "green" : "red";
        var word = succeeded ? "Succeeded" : "Failed";
        AnsiConsole.MarkupLine(
            $"  [{color}]{word} with Rights: {counts.Rights}, Wrongs: {counts.Wrongs}, Errors: {counts.Errors}[/]");
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

    /// <summary>
    /// Where the run spent its time (issue #142): the slowest scenarios with their share of the
    /// measured time, what the costliest grammar steps and lifecycle points cost across the
    /// whole suite, the largest stretches no stop point owns, and the scenarios that assert
    /// nothing. Report, don't act — evidence for a judgement, never a verdict. Silent when
    /// nothing was measured and nothing is flagged. The console shows a summary; the JSON
    /// artifact carries every figure.
    /// </summary>
    public void RenderTimingSummary(SuiteTiming timing)
    {
        if (!timing.IsMeasured && timing.WithoutAssertions.Count == 0) return;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"  [bold]Timing[/] [grey]— {SuiteTiming.Humanize(timing.Measured)} measured across " +
            $"{timing.Scenarios.Count} scenario(s){(timing.Unmeasured > 0 ? $", {timing.Unmeasured} unmeasured (figures are a floor)" : "")}[/]");

        foreach (var scenario in timing.Slowest(3))
        {
            var share = timing.Share(scenario.WallClock);
            var shareText = share is { } s ? $" ({SuiteTiming.Percent(s)} of measured time)" : "";
            AnsiConsole.MarkupLine(
                $"    [grey]•[/] {Markup.Escape(scenario.Title)} " +
                $"[grey]{SuiteTiming.Humanize(scenario.WallClock)}{shareText} — " +
                $"steps {SuiteTiming.Humanize(scenario.Steps)}, lifecycle {SuiteTiming.Humanize(scenario.Lifecycle)}[/]");
        }

        foreach (var step in timing.Steps.Take(3))
        {
            AnsiConsole.MarkupLine(
                $"    [grey]step[/] [italic]{Markup.Escape(step.Text)}[/] " +
                $"[grey]cost {SuiteTiming.Humanize(step.Total)} across {step.Occurrences} occurrence(s)[/]");
        }

        foreach (var point in timing.Lifecycle.Take(3))
        {
            AnsiConsole.MarkupLine(
                $"    [grey]lifecycle[/] {Markup.Escape(point.Text)} " +
                $"[grey]cost {SuiteTiming.Humanize(point.Total)} across {point.Occurrences} scenario(s)[/]");
        }

        // A 100ms console floor keeps micro-gaps (observer callbacks, loop overhead) from
        // burying the real ones; the JSON carries them all, so nothing is silently dropped.
        var notable = timing.Gaps.Where(g => g.Duration >= TimeSpan.FromMilliseconds(100)).ToList();
        foreach (var gap in notable.Take(3))
        {
            AnsiConsole.MarkupLine(
                $"    [yellow]⏳ {SuiteTiming.Humanize(gap.Duration)} unowned[/] [grey]in {Markup.Escape(gap.Scenario)} " +
                $"between '{Markup.Escape(gap.After)}' and '{Markup.Escape(gap.Before)}'[/]");
        }

        if (notable.Count > 3)
        {
            AnsiConsole.MarkupLine($"    [grey]… {notable.Count - 3} more gap(s) over 100ms in the JSON output[/]");
        }

        if (timing.WithoutAssertions.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]⚠ {timing.WithoutAssertions.Count} scenario(s) ran steps but asserted nothing[/]");
            foreach (var uid in timing.WithoutAssertions)
            {
                AnsiConsole.MarkupLine($"    [yellow]•[/] {Markup.Escape(uid)}");
            }
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
