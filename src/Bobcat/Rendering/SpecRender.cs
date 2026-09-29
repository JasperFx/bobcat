using Bobcat.Engine;

namespace Bobcat.Rendering;

/// <summary>
/// Intermediate rendering model for a single scenario's results.
/// Feeds both Spectre.Console and later HTML rendering.
/// </summary>
public class SpecRender
{
    public string Title { get; init; } = "";
    public string? FeatureTitle { get; init; }
    public bool Succeeded { get; init; }
    public List<StepRender> Steps { get; init; } = new();
    public Counts Counts { get; init; } = new();

    /// <summary>
    /// The scenario's wall clock across the whole bracket — reset, scope, hooks, steps,
    /// teardown (issue #141). Falls back to the last step's end offset only for results that
    /// carried no wall clock (a bare executor in a test, an older artifact), which
    /// under-reports by exactly the lifecycle time.
    /// </summary>
    public long DurationMs { get; init; }

    /// <summary>
    /// The named non-step stop points — BeforeEach, the ResetAll/BeginScenarioAll bracket,
    /// EndScenarioAll — on the same clock the steps' <see cref="StepRender.StartedAtMs"/> uses.
    /// </summary>
    public List<TimelinePointRender> Timeline { get; init; } = new();

    /// <summary>
    /// Why the scenario failed, when no step accounts for it — an assertion library that threw
    /// between steps, an exception before the first one, a runner verdict Bobcat never saw the cause
    /// of.
    /// </summary>
    /// <remarks>
    /// Null whenever the steps already explain the failure, because repeating it there would turn
    /// the one line a reader most needs into boilerplate they learn to skip. It is set precisely for
    /// the case a projected spec hits most often: a test asserting with its own library, where the
    /// narrative is declared and the verdict is an exception.
    /// </remarks>
    public string? ScenarioFailure { get; init; }

    /// <summary>
    /// Expected/actual pairs recovered from <see cref="ScenarioFailure"/> by its renderer — a Shouldly
    /// message read as the cell it already contains.
    /// </summary>
    public List<CellRender> ScenarioFailureCells { get; init; } = new();

    public static SpecRender FromResults(string title, ExecutionResults results, string? featureTitle = null)
    {
        var steps = results.Steps.Select(StepRender.FromStepResult).ToList();
        var durationMs = results.WallClockMs > 0
            ? results.WallClockMs
            : results.Steps.Where(s => s.End > 0).Select(s => s.End).DefaultIfEmpty(0).Max();

        return new SpecRender
        {
            Title = title,
            FeatureTitle = featureTitle,
            Succeeded = results.Counts.Succeeded,
            Steps = steps,
            Counts = results.Counts,
            DurationMs = durationMs,
            Timeline = results.Timeline
                .Select(p => new TimelinePointRender
                {
                    Name = p.Name,
                    StartedAtMs = p.StartMs,
                    DurationMs = p.DurationMs
                })
                .ToList()
        };
    }

    /// <summary>
    /// The same rendering model, built from a <b>projected</b> test — an ordinary xUnit or TUnit
    /// test whose steps were declared by marker comments and recorded by <c>[BobcatStep]</c>
    /// helpers (issue #110).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the projected lane renders through the same model.</b> A projected spec and a
    /// <c>.feature</c> spec are the same artifact seen from two authoring styles, so they have to
    /// reach the console, the JSON report and any later HTML through one model — otherwise
    /// "Bobcat's spec output" quietly means two different things and only one of them gets improved.
    /// </para>
    /// <para>
    /// <b>Declared is not executed, and the render says which is which.</b> The steps a test
    /// DECLARED in comments are its narrative; the steps it RECORDED are what ran. When both exist
    /// the declared narrative is the backbone and each recorded step renders nested under the
    /// comment it ran inside — the join is <c>DeclaredStepNumber</c>, a compile-time fact, never a
    /// guess from timing. A declared step nothing ran inside is rendered as
    /// <see cref="ResultStatus.ok"/>: it is prose, and claiming it passed would be inventing a
    /// verdict for a sentence.
    /// </para>
    /// </remarks>
    public static SpecRender FromRecording(ScenarioRecorder.Recording recording)
    {
        var declared = recording.Declared;
        var planned = PlannedSteps.For(recording.Uid);
        var recorded = recording.Steps;

        var steps = planned.Count > 0 && recorded.Where(x => x.Depth == 0).All(x => x.PlannedStepNumber is not null)
            ? alongThePlan(declared, planned, recorded)
            : withoutAPlan(declared, recorded);

        var counts = recording.Counts;
        var unexplained = unexplainedFailure(recording, steps);

        if (unexplained is not null)
        {
            // Counted, so the figures and the heading agree. A narrated test whose only failure is its
            // own assertion library's used to render `Succeeded with Rights: 0, Wrongs: 0, Errors: 0`
            // under a FAILED heading — truthful about what was RECORDED and useless to read.
            counts = new Counts(counts.Rights, counts.Wrongs, counts.Errors);
            counts.Read(unexplained.Kind == SpecFailureKind.Assertion
                ? ResultStatus.failed
                : ResultStatus.error);
        }

        return new SpecRender
        {
            Title = recording.Scenario,
            FeatureTitle = recording.Feature,
            Succeeded = counts.Succeeded && unexplained is null && recording.FailureDescription is null,
            Steps = steps,
            Counts = counts,
            ScenarioFailure = unexplained?.Message,
            ScenarioFailureCells = unexplained?.Cells.Select(CellRender.From).ToList() ?? [],
            DurationMs = recorded.Count > 0
                ? recorded.Max(x => x.EndedAtMs ?? x.StartedAtMs)
                : 0
        };
    }

    /// <summary>
    /// The render when the scenario's plan is known: every step the test was GOING to take, in
    /// source order, with what happened at each one — and the ones it never reached shown as never
    /// reached rather than omitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declared comments and planned grammar calls are merged <b>by source line</b>, which is the
    /// one ordering both of them actually carry. Nothing is inferred from execution order: a step
    /// that never ran has no execution order to be inferred from, and that is precisely the step
    /// this method exists to render.
    /// </para>
    /// <para>
    /// A planned step may match more than one recorded step — a helper called inside a loop — and
    /// all of them render, because each one really ran.
    /// </para>
    /// </remarks>
    private static List<StepRender> alongThePlan(
        IReadOnlyList<DeclaredStep> declared,
        IReadOnlyList<PlannedStep> planned,
        IReadOnlyList<ScenarioRecorder.RecordedStep> recorded)
    {
        var steps = new List<StepRender>();

        var byDeclared = recorded
            .Where(x => x.DeclaredStepNumber is not null)
            .GroupBy(x => x.DeclaredStepNumber!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ScenarioRecorder.RecordedStep>)g.ToList());

        var timeline = declared
            .Select((d, i) => (d.Line, Declared: (DeclaredStep?)d, Index: i, Planned: (PlannedStep?)null))
            .Concat(planned.Select((p, i) =>
                (p.Line, Declared: (DeclaredStep?)null, Index: i, Planned: (PlannedStep?)p)))
            .OrderBy(x => x.Line)
            .ToList();

        foreach (var entry in timeline)
        {
            if (entry.Declared is { } comment)
            {
                var inside = byDeclared.TryGetValue(entry.Index + 1, out var found) ? found : [];
                steps.Add(StepRender.FromDeclaredStep(comment, entry.Index, inside));
                continue;
            }

            var step = entry.Planned!;
            var depth = step.DeclaredStepNumber is null ? 0 : 1;

            var matches = recorded.Where(x => x.PlannedStepNumber == entry.Index + 1).ToList();
            if (matches.Count == 0)
            {
                steps.Add(StepRender.NotReached(step, depth));
                continue;
            }

            foreach (var match in matches)
            {
                steps.Add(StepRender.FromRecordedStep(match, depth));
                steps.AddRange(descendantsOf(match, depth + 1));
            }
        }

        return steps;
    }

    private static IEnumerable<StepRender> descendantsOf(ScenarioRecorder.RecordedStep step, int depth)
    {
        foreach (var child in step.Children)
        {
            yield return StepRender.FromRecordedStep(child, depth);

            foreach (var grandchild in descendantsOf(child, depth + 1)) yield return grandchild;
        }
    }

    /// <summary>
    /// The scenario's failure when no step accounts for it, read through
    /// <see cref="SpecFailureRenderers"/> — which is what turns a narrated test's Shouldly wall of text
    /// into a named expected/actual cell.
    /// </summary>
    private static SpecFailure? unexplainedFailure(
        ScenarioRecorder.Recording recording, List<StepRender> steps)
    {
        if (steps.Any(x => x.Status is not (ResultStatus.success or ResultStatus.ok))) return null;

        if (recording.Failure is { } exception)
        {
            return SpecFailureRenderers.Render(SpecFailureContext.From(exception));
        }

        if (recording.FailureDescription is not { Length: > 0 } described) return null;

        // The runner hands over a TYPE NAME and a MESSAGE, never the exception — xUnit v3 reports
        // ExceptionTypes and ExceptionMessages on TestContext.TestState. `Describe()` joined them with
        // ": ", so they are split back apart here rather than the registry being given prose.
        var colon = described.IndexOf(": ", StringComparison.Ordinal);
        var typeName = colon > 0 ? described[..colon] : "";
        var message = colon > 0 ? described[(colon + 2)..] : described;

        var simpleName = typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;

        return SpecFailureRenderers.Render(new SpecFailureContext(simpleName, message));
    }

    /// <summary>
    /// The render for a scenario with no plan behind it — a hand-written <c>ScenarioRecorder.Step</c>
    /// call, or a comment-only narration. Recorded order, and no not-reached rows: without a plan
    /// there is nothing that says a step was ever going to be taken.
    /// </summary>
    private static List<StepRender> withoutAPlan(
        IReadOnlyList<DeclaredStep> declared, IReadOnlyList<ScenarioRecorder.RecordedStep> recorded)
    {
        var steps = new List<StepRender>();

        var byDeclared = recorded
            .Where(x => x.DeclaredStepNumber is not null)
            .GroupBy(x => x.DeclaredStepNumber!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ScenarioRecorder.RecordedStep>)g.ToList());

        var loose = recorded.Where(x => x.DeclaredStepNumber is null && x.Depth == 0).ToList();

        for (var i = 0; i < declared.Count; i++)
        {
            var inside = byDeclared.TryGetValue(i + 1, out var found) ? found : [];
            steps.Add(StepRender.FromDeclaredStep(declared[i], i, inside));

            foreach (var step in inside.Where(x => x.Depth == 0))
            {
                steps.Add(StepRender.FromRecordedStep(step, depth: 1));
                steps.AddRange(descendantsOf(step, 2));
            }
        }

        var baseDepth = declared.Count > 0 ? 1 : 0;
        foreach (var step in loose)
        {
            steps.Add(StepRender.FromRecordedStep(step, baseDepth));
            steps.AddRange(descendantsOf(step, baseDepth + 1));
        }

        return steps;
    }
}

/// <summary>
/// A named lifecycle stop point on the scenario's timeline (issue #141).
/// </summary>
public class TimelinePointRender
{
    public string Name { get; init; } = "";
    public long StartedAtMs { get; init; }
    public long DurationMs { get; init; }
}

/// <summary>
/// Rendering model for a single step.
/// </summary>
public class StepRender
{
    public string StepId { get; init; } = "";
    public StepKind Kind { get; init; }

    /// <summary>
    /// The step's keyword exactly as the specification wrote it, when that is knowable — including
    /// <c>And</c> and <c>But</c>, which <see cref="StepKind"/> has no member for because they are
    /// not a different kind of step, only a different way of writing one.
    /// </summary>
    /// <remarks>
    /// Null for a step whose keyword was never recorded, and a renderer then falls back to
    /// <see cref="Kind"/>. Carried rather than derived because <c>And</c> is a fact about the
    /// scenario — whether a step is the first of its block or the third — and only the recorder
    /// knew it.
    /// </remarks>
    public string? Keyword { get; init; }

    /// <summary>
    /// Nesting level: 0 for a step in the scenario's own narrative, 1 for a step that ran inside
    /// one. Exists so a projected test's grammar-helper calls can be shown under the marker comment
    /// they ran inside without flattening the two into one indistinguishable list.
    /// </summary>
    public int Depth { get; init; }

    /// <summary>
    /// Whether this step is <b>narrative</b> — a marker comment, whose verdict is only an aggregate
    /// of whatever ran inside it. A narrative step makes no claim of its own, so a renderer must not
    /// attribute a failure to it.
    /// </summary>
    public bool IsNarrative { get; init; }

    /// <summary>
    /// A step the scenario planned and never reached — because something earlier stopped it.
    /// </summary>
    /// <remarks>
    /// Storyteller greyed these out, and the reason is worth restating: the alternative is that they
    /// are simply absent, and a reader cannot tell a specification that asserted four things from
    /// one that meant to assert six and fell over at the third. Only knowable because the plan is a
    /// compile-time fact (<see cref="PlannedSteps"/>).
    /// </remarks>
    public bool NotRun { get; init; }

    /// <summary>
    /// Where this step's INPUT values sit in <see cref="StepText"/>, so a renderer can set them
    /// apart — italics, the way Storyteller rendered a sentence's input cells.
    /// </summary>
    public IReadOnlyList<StepTextSpan> ValueSpans { get; init; } = [];

    /// <summary>
    /// The exception itself, when the step ended in one and the renderer can have it — a console
    /// renderer formats a real exception far better than a message and a type name.
    /// </summary>
    /// <remarks>
    /// Null for a gathered wrong (nothing was thrown, so there is no stack to show) and null
    /// wherever the failure crossed a process boundary, where a name and a message is all there
    /// ever is.
    /// </remarks>
    public Exception? Exception { get; init; }

    public string StepText { get; init; } = "";
    public ResultStatus Status { get; init; }
    public FailureLevel FailureLevel { get; init; }

    /// <summary>
    /// Milliseconds from the scenario's announced start to this step starting (issue #141).
    /// Kept alongside the duration deliberately: durations say what the steps cost, offsets
    /// say what the steps did NOT cost — the gap between one step's end and the next one's
    /// start is time no step owns.
    /// </summary>
    public long StartedAtMs { get; init; }

    public long DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ExceptionType { get; init; }
    public SetVerificationRender? SetVerification { get; init; }
    public List<CellRender> Cells { get; init; } = new();
    public List<string> Logs { get; init; } = new();
    public Dictionary<string, string> Diagnostics { get; init; } = new();

    public static StepRender FromStepResult(StepResult result)
    {
        SetVerificationRender? sv = null;
        List<CellRender> cells = new();

        if (result.IsSetVerification && result.SetVerificationColumns != null)
        {
            sv = SetVerificationRender.FromStepResult(result);
        }
        else
        {
            cells = result.Cells.Select(c => new CellRender
            {
                Name = c.Name,
                Status = c.Status,
                DisplayText = c.DisplayText,
                Expected = c.Expected,
                Actual = c.Actual,
                Note = c.Note
            }).ToList();
        }

        return new StepRender
        {
            StepId = result.StepId,
            Kind = result.StepKind,
            StepText = result.StepText ?? result.StepId,
            Status = result.StepStatus,
            FailureLevel = result.FailureLevel,
            StartedAtMs = result.Start,
            DurationMs = result.End > result.Start ? result.End - result.Start : 0,
            ErrorMessage = result.Exception?.Message,
            ExceptionType = result.Exception?.GetType().Name,
            SetVerification = sv,
            Cells = cells,
            Logs = result.Logs.ToList(),
            Diagnostics = result.Diagnostics.ToDictionary(
                kv => kv.Key, kv => kv.Value?.ToString() ?? "")
        };
    }

    /// <summary>Whether the step's own cells already account for its failure.</summary>
    private static bool explainedByCells(ScenarioRecorder.RecordedStep step)
        => step.Cells.Any(x => x.Status is not (ResultStatus.success or ResultStatus.ok));

    /// <summary>One step a projected test actually ran — a <c>[BobcatStep]</c> helper call.</summary>
    public static StepRender FromRecordedStep(ScenarioRecorder.RecordedStep step, int depth = 0)
    {
        var rendered = step.Failure is null
            ? null
            : SpecFailureRenderers.Render(SpecFailureContext.From(step.Failure));

        return new StepRender
        {
            StepId = step.StepId,
            Kind = KindOf(step.Keyword),
            // Verbatim, empty string included: "" means the step spells no keyword and must get no
            // label, while null means "unknown, fall back to the kind". Collapsing the two printed
            // `Then` over a Storyteller-style sentence that never claimed to be one.
            Keyword = step.Keyword,
            Depth = depth,
            StepText = step.Text,
            Status = step.Status,
            FailureLevel = step.Failure is null || ProjectedFailure.IsAssertion(step.Failure)
                ? FailureLevel.Assertion
                : FailureLevel.Critical,
            StartedAtMs = step.StartedAtMs,
            DurationMs = step.DurationMs is > 0 ? step.DurationMs.Value : 0,

            // A gathered wrong has no exception TYPE worth showing — SpecAssert.Fail's
            // SpecAssertionException was never thrown, and naming it would make a clean failure
            // message look like a crash.
            // Suppressed when a cell already reports the disagreement. The cell says it in one line and
            // in the shape every other comparison in the report uses, and for a projected assertion the
            // library's own message is actively WORSE — intercepting the call moves it away from the
            // source Shouldly reads its subject expression out of.
            ErrorMessage = explainedByCells(step) || rendered?.Message is not { Length: > 0 }
                ? null
                : rendered.Message,
            ExceptionType = rendered?.ShowStackTrace == true ? step.Failure!.GetType().Name : null,
            ValueSpans = step.ValueSpans,

            // Only when the failure's own renderer says a stack is worth showing. A gathered wrong's
            // SpecAssertionException was never thrown, and an assertion library's failure has a message
            // that already says everything — handing either to an exception formatter dresses a clean
            // failure up as a crash.
            Exception = rendered?.ShowStackTrace == true ? step.Failure : null,

            // The step's own cells, plus any the failure's renderer recovered from its message.
            Cells = step.Cells.Select(CellRender.From)
                .Concat(rendered?.Cells.Select(CellRender.From) ?? [])
                .ToList()
        };
    }

    /// <summary>
    /// One step a projected test <b>declared</b> in a marker comment, carrying the verdict of
    /// whatever ran inside it.
    /// </summary>
    /// <remarks>
    /// The status is an aggregate and nothing else: worst-of the steps inside, and
    /// <see cref="ResultStatus.ok"/> when nothing ran inside at all. A comment is prose — it makes
    /// no claim of its own, so it can neither pass nor fail on its own account, and rendering it
    /// green because the test as a whole passed is how a narrative starts lying.
    /// </remarks>
    public static StepRender FromDeclaredStep(
        DeclaredStep declared, int index, IReadOnlyList<ScenarioRecorder.RecordedStep> inside)
    {
        var status = inside.Count == 0
            ? ResultStatus.ok
            : inside.Select(x => x.Status).Aggregate(ResultStatus.success, worse);

        return new StepRender
        {
            StepId = "d" + (index + 1),
            IsNarrative = true,
            Kind = KindOf(declared.Keyword),
            Keyword = declared.Keyword,
            StepText = declared.Text,
            Status = status,
            StartedAtMs = inside.Count > 0 ? inside[0].StartedAtMs : 0,
            DurationMs = inside.Count > 0
                ? Math.Max(0, (inside[^1].EndedAtMs ?? inside[^1].StartedAtMs) - inside[0].StartedAtMs)
                : 0
        };
    }

    /// <summary>A planned step the scenario never reached.</summary>
    public static StepRender NotReached(PlannedStep planned, int depth)
        => new()
        {
            StepId = "p" + planned.Line,
            Kind = KindOf(planned.Keyword),
            Keyword = planned.Keyword,
            Depth = depth,

            // The TEMPLATE, placeholders and all. The arguments were never evaluated, so there are
            // no values to show, and inventing them would describe a run that did not happen.
            StepText = planned.Template,
            Status = ResultStatus.ok,
            NotRun = true
        };

    private static ResultStatus worse(ResultStatus left, ResultStatus right)
        => rank(right) > rank(left) ? right : left;

    private static int rank(ResultStatus status)
        => status switch
        {
            ResultStatus.ok => 0,
            ResultStatus.success => 1,
            ResultStatus.failed => 2,
            _ => 3
        };

    /// <summary>
    /// A Gherkin keyword as the kind of step it is. <c>And</c> and <c>But</c> have no kind of their
    /// own — the recorder already decided they continue the block they sit in — so they map to
    /// <see cref="StepKind.Then"/>'s neighbour only through <see cref="Keyword"/>, and a renderer
    /// should prefer that.
    /// </summary>
    public static StepKind KindOf(string keyword)
        => keyword.ToLowerInvariant() switch
        {
            "given" => StepKind.Given,
            "when" => StepKind.When,
            "then" => StepKind.Then,
            _ => StepKind.Then
        };
}

/// <summary>
/// Rendering model for a cell (non-table result).
/// </summary>
public class CellRender
{
    public string Name { get; init; } = "";
    public ResultStatus Status { get; init; }
    public string DisplayText { get; init; } = "";
    public string? Expected { get; init; }
    public string? Actual { get; init; }
    public string? Note { get; init; }

    public static CellRender From(CellResult cell)
        => new()
        {
            Name = cell.Name,
            Status = cell.Status,
            DisplayText = cell.DisplayText,
            Expected = cell.Expected,
            Actual = cell.Actual,
            Note = cell.Note
        };
}

/// <summary>
/// Rendering model for set verification table results.
/// </summary>
public class SetVerificationRender
{
    public List<string> Columns { get; init; } = new();
    public List<SetVerificationRowRender> Rows { get; init; } = new();

    public static SetVerificationRender FromStepResult(StepResult result)
    {
        var columns = result.SetVerificationColumns?.ToList() ?? new();
        var rows = new List<SetVerificationRowRender>();

        foreach (var group in result.Cells.GroupBy(c => c.RowIndex).OrderBy(g => g.Key))
        {
            var cells = group.ToList();
            var missingCell = cells.FirstOrDefault(c => c.Name == "missing-row");
            var extraCell = cells.FirstOrDefault(c => c.Name == "extra-row");

            if (missingCell != null)
            {
                // The comparer emits the expected values per column beside the marker cell,
                // so the grid can show which row was missing. Older producers emitted only
                // the marker; then the row carries no cells and the renderer says so.
                rows.Add(rowOfAbsent(SetVerificationRowType.Missing, missingCell, cells, columns,
                    c => c.Expected));
            }
            else if (extraCell != null)
            {
                rows.Add(rowOfAbsent(SetVerificationRowType.Extra, extraCell, cells, columns,
                    c => c.Actual));
            }
            else
            {
                var row = new SetVerificationRowRender { RowType = SetVerificationRowType.Matched };
                foreach (var col in columns)
                {
                    var cell = cells.FirstOrDefault(c => c.Name == col);
                    row.Cells.Add(new SetVerificationCellRender
                    {
                        Column = col,
                        Status = cell?.Status ?? ResultStatus.ok,
                        DisplayText = cell?.DisplayText ?? "",
                        Expected = cell?.Expected,
                        Actual = cell?.Actual,
                        Note = cell?.Note
                    });
                }
                rows.Add(row);
            }
        }

        return new SetVerificationRender { Columns = columns, Rows = rows };
    }

    /// <summary>
    /// A row present on only one side: the marker cell carries the human description and the
    /// row's verdict, while the per-column cells beside it carry the values to show in place.
    /// </summary>
    private static SetVerificationRowRender rowOfAbsent(
        SetVerificationRowType rowType,
        CellResult marker,
        List<CellResult> cells,
        List<string> columns,
        Func<CellResult, string?> valueOf)
    {
        var row = new SetVerificationRowRender
        {
            RowType = rowType,
            Description = marker.DisplayText
        };

        var byColumn = columns
            .Select(col => (col, cell: cells.FirstOrDefault(c => c.Name == col)))
            .ToList();

        if (byColumn.All(x => x.cell == null)) return row;

        foreach (var (col, cell) in byColumn)
        {
            row.Cells.Add(new SetVerificationCellRender
            {
                Column = col,
                Status = marker.Status,
                DisplayText = (cell == null ? null : valueOf(cell)) ?? "",
                Expected = cell?.Expected,
                Actual = cell?.Actual
            });
        }

        return row;
    }
}

public class SetVerificationRowRender
{
    public SetVerificationRowType RowType { get; init; }
    public List<SetVerificationCellRender> Cells { get; init; } = new();
    public string? Description { get; init; }

    /// <summary>
    /// A row passes when it has no bad cells. Plain input/echo cells (status <c>ok</c>)
    /// from decision tables do not count against the row.
    /// </summary>
    public bool AllCellsOk => Cells.All(c =>
        c.Status is not (ResultStatus.failed or ResultStatus.invalid
            or ResultStatus.error or ResultStatus.missing));
}

public class SetVerificationCellRender
{
    public string Column { get; init; } = "";
    public ResultStatus Status { get; init; }
    public string DisplayText { get; init; } = "";
    public string? Expected { get; init; }
    public string? Actual { get; init; }
    public string? Note { get; init; }
}

public enum SetVerificationRowType
{
    Matched,
    Missing,
    Extra
}
