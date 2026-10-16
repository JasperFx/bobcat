using System.Text.Json;
using System.Text.Json.Serialization;
using Bobcat.Engine;
using Bobcat.Runtime;

namespace Bobcat.Rendering;

/// <summary>
/// Renders scenario results and suite timing as structured JSON.
/// Designed for AI consumption via MCP diagnose_failing_spec tool.
/// </summary>
public static class JsonRenderer
{
    internal static readonly JsonSerializerOptions options = new()
    {
        // A resolver set explicitly so the results file's schema (issue #540) is exported from the
        // very options the file is written with -- one description of the shape, not two.
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// The timing block on its own, for a projected suite, read through
    /// <see cref="SuiteTiming.For(IEnumerable{ScenarioRecorder.Recording})"/> (issue #466).
    /// <c>null</c> when nothing was measured and nothing is flagged.
    /// </summary>
    public static string RenderTiming(SuiteTiming timing)
        => JsonSerializer.Serialize(timingToJson(timing), options);

    internal static JsonTimingOutput? timingToJson(SuiteTiming timing)
    {
        if (!timing.IsMeasured && timing.WithoutAssertions.Count == 0 && timing.Unmeasured == 0) return null;

        return new JsonTimingOutput
        {
            MeasuredMs = (long)timing.Measured.TotalMilliseconds,
            Unmeasured = timing.Unmeasured > 0 ? timing.Unmeasured : null,
            Scenarios = timing.Scenarios.Select(s => new JsonScenarioTimingOutput
            {
                Feature = s.Feature,
                Title = s.Title,
                WallClockMs = (long)s.WallClock.TotalMilliseconds,
                StepsMs = (long)s.Steps.TotalMilliseconds,
                LifecycleMs = s.Lifecycle is { } lifecycle ? (long)lifecycle.TotalMilliseconds : null,
                UnownedMs = (long)s.Unowned.TotalMilliseconds
            }).ToList(),
            Steps = timing.Steps.Select(costToJson).ToList(),
            // Absent, not empty, when it was never measured: [] would read as "no lifecycle cost".
            Lifecycle = timing.LifecycleMeasured ? timing.Lifecycle.Select(costToJson).ToList() : null,
            LifecycleMeasured = timing.LifecycleMeasured ? null : false,
            Gaps = timing.Gaps.Count > 0
                ? timing.Gaps.Select(g => new JsonGapOutput
                {
                    Feature = g.Feature,
                    Scenario = g.Scenario,
                    After = g.After,
                    Before = g.Before,
                    DurationMs = (long)g.Duration.TotalMilliseconds
                }).ToList()
                : null,
            WithoutAssertions = timing.WithoutAssertions.Count > 0 ? timing.WithoutAssertions.ToList() : null
        };
    }

    private static JsonStepCostOutput costToJson(StepCost cost) => new()
    {
        Text = cost.Text,
        Occurrences = cost.Occurrences,
        TotalMs = (long)cost.Total.TotalMilliseconds,
        MaxMs = (long)cost.Max.TotalMilliseconds
    };

    public static string RenderScenario(SpecRender spec)
    {
        return JsonSerializer.Serialize(specToJson(spec), options);
    }

    internal static JsonScenarioOutput specToJson(SpecRender spec)
    {
        return new JsonScenarioOutput
        {
            Id = spec.FeatureTitle is null ? null : $"{spec.FeatureTitle}/{spec.Title}",
            Title = spec.Title,
            Feature = spec.FeatureTitle,
            Succeeded = spec.Succeeded,
            Verdict = spec.Verdict,
            SpecLifecycle = spec.Lifecycle == Lifecycle.Acceptance ? "acceptance" : null,
            Counts = countsToJson(spec.Counts),
            DurationMs = spec.DurationMs,
            StepsMs = spec.StepsMs,
            Lifecycle = spec.Timeline.Count > 0
                ? spec.Timeline.Select(p => new JsonTimelinePointOutput
                {
                    Name = p.Name,
                    StartedAtMs = p.StartedAtMs,
                    DurationMs = p.DurationMs
                }).ToList()
                : null,
            Steps = spec.Steps.Select(stepToJson).ToList(),
            ScenarioFailure = spec.ScenarioFailure,
            ScenarioFailureCells = spec.ScenarioFailureCells.Count > 0
                ? spec.ScenarioFailureCells.Select(c => cellToJson(c.Name, null, c.Status, c.Expected, c.Actual, c.Note, c.DisplayText)).ToList()
                : null,

            // The scenario's reports (issue #408) — the agent-readable half of the feature, in the
            // same cell shape every other grid here uses. Already filtered by visibility, so a
            // passing scenario carries none unless the run asked for verbose.
            Reports = spec.Reports.Count > 0
                ? spec.Reports.Select(reportToJson).ToList()
                : null,

            // SpecLog lines written between steps (bobcat#547), latched like reports.
            Logs = spec.Logs.Count > 0 ? spec.Logs : null,
            SuppressedLogs = spec.SuppressedLogs > 0 ? spec.SuppressedLogs : null,

            // Failure evidence (issue #571): references, never bytes. Latched like reports.
            Attachments = attachmentsToJson(spec.Attachments),
            SuppressedAttachments = spec.SuppressedAttachments > 0 ? spec.SuppressedAttachments : null
        };
    }

    private static List<JsonAttachmentOutput>? attachmentsToJson(List<AttachmentRender> attachments)
        => attachments.Count > 0
            ? attachments.Select(a => new JsonAttachmentOutput
            {
                Name = a.Name,
                MediaType = a.MediaType,
                Size = a.Size,
                Path = a.Path,
                Omitted = a.Omitted
            }).ToList()
            : null;

    private static JsonReportOutput reportToJson(ReportRender report)
    {
        return new JsonReportOutput
        {
            Title = report.Title,
            ShortTitle = report.ShortTitle,
            SuppressedRows = report.SuppressedRows > 0 ? report.SuppressedRows : null,
            Grid = svToJson(report.Grid)
        };
    }

    private static JsonStepOutput stepToJson(StepRender step)
    {
        return new JsonStepOutput
        {
            StepId = step.StepId,
            Kind = step.Kind.ToString(),
            Keyword = step.Keyword,
            Depth = step.Depth > 0 ? step.Depth : null,
            Narrative = step.IsNarrative ? true : null,
            NotRun = step.NotRun ? true : null,
            Text = step.StepText,
            Status = step.Status.ToString(),
            FailureLevel = step.FailureLevel != FailureLevel.None ? step.FailureLevel.ToString() : null,
            StartedAtMs = step.StartedAtMs,
            DurationMs = step.DurationMs > 0 ? step.DurationMs : null,
            Error = step.ErrorMessage,
            ExceptionType = step.ExceptionType,
            Logs = step.Logs.Count > 0 ? step.Logs : null,
            SuppressedLogs = step.SuppressedLogs > 0 ? step.SuppressedLogs : null,
            Diagnostics = step.Diagnostics.Count > 0 ? step.Diagnostics : null,
            Cells = step.Cells.Count > 0
                ? step.Cells.Select(c => cellToJson(c.Name, null, c.Status, c.Expected, c.Actual, c.Note, c.DisplayText)).ToList()
                : null,
            SetVerification = step.SetVerification != null ? svToJson(step.SetVerification) : null,
            Attachments = attachmentsToJson(step.Attachments)
        };
    }

    private static JsonSetVerificationOutput svToJson(SetVerificationRender sv)
    {
        var rows = new List<JsonSvRowOutput>();
        var rowIndex = 0;
        foreach (var r in sv.Rows)
        {
            rows.Add(new JsonSvRowOutput
            {
                Row = rowIndex,
                Type = r.RowType.ToString(),
                Cells = r.Cells.Count > 0
                    ? r.Cells.Select(c => cellToJson(c.Column, rowIndex, c.Status, c.Expected, c.Actual, c.Note, c.DisplayText)).ToList()
                    : null,
                Description = r.Description
            });
            rowIndex++;
        }

        return new JsonSetVerificationOutput
        {
            Columns = sv.Columns,
            Rows = rows
        };
    }

    /// <summary>
    /// Build the AI-optimized structured cell. Emits expected/actual/note when the cell
    /// carried a typed comparison; falls back to a plain value for input/echo cells.
    /// </summary>
    private static JsonCellOutput cellToJson(string column, int? row, ResultStatus status,
        string? expected, string? actual, string? note, string displayText)
    {
        var hasStructured = expected != null || actual != null;
        return new JsonCellOutput
        {
            Column = column,
            Row = row,
            Status = status.ToString(),
            Expected = expected,
            Actual = actual,
            Note = note,
            Value = hasStructured ? null : displayText
        };
    }

    private static JsonCountsOutput countsToJson(Counts counts)
    {
        return new JsonCountsOutput
        {
            Rights = counts.Rights,
            Wrongs = counts.Wrongs,
            Errors = counts.Errors,
            Succeeded = counts.Succeeded
        };
    }
}

// JSON output models — kept internal, serialized by JsonRenderer

internal class JsonTimingOutput
{
    public long MeasuredMs { get; set; }

    /// <summary>Scenarios with no wall clock — the figures above are a floor, never zero-filled.</summary>
    public int? Unmeasured { get; set; }

    /// <summary>Every measured scenario, slowest first.</summary>
    public List<JsonScenarioTimingOutput> Scenarios { get; set; } = new();

    /// <summary>Per normalized step text across the suite, costliest first.</summary>
    public List<JsonStepCostOutput> Steps { get; set; } = new();

    /// <summary>
    /// Per lifecycle point across the suite, costliest first. Absent when the lifecycle was not
    /// measured -- see <see cref="LifecycleMeasured"/>.
    /// </summary>
    public List<JsonStepCostOutput>? Lifecycle { get; set; }

    /// <summary>
    /// <c>false</c> for projected input, whose lifecycle belongs to the test runner and is not
    /// measured (issue #466); absent in an artifact written by the removed Gherkin runner, which
    /// always measured it. Additive, so such an artifact reads exactly as it did.
    /// </summary>
    public bool? LifecycleMeasured { get; set; }

    /// <summary>Time no stop point owns, largest first.</summary>
    public List<JsonGapOutput>? Gaps { get; set; }

    /// <summary>Scenarios that ran steps but asserted nothing — no Then step, no comparison cell.</summary>
    public List<string>? WithoutAssertions { get; set; }
}

internal class JsonScenarioTimingOutput
{
    public string Feature { get; set; } = "";
    public string Title { get; set; } = "";
    public long WallClockMs { get; set; }
    public long StepsMs { get; set; }
    /// <summary>Absent when the lifecycle was not measured (projected input), never zero-filled.</summary>
    public long? LifecycleMs { get; set; }

    /// <summary>
    /// Wall clock no step or lifecycle point owns. With the lifecycle unmeasured it is simply the
    /// time outside the steps, unattributed.
    /// </summary>
    public long UnownedMs { get; set; }
}

internal class JsonStepCostOutput
{
    public string Text { get; set; } = "";
    public int Occurrences { get; set; }
    public long TotalMs { get; set; }
    public long MaxMs { get; set; }
}

internal class JsonGapOutput
{
    public string Feature { get; set; } = "";
    public string Scenario { get; set; } = "";
    public string After { get; set; } = "";
    public string Before { get; set; } = "";
    public long DurationMs { get; set; }
}

internal class JsonScenarioOutput
{
    public List<JsonReportOutput>? Reports { get; set; }

    /// <summary>
    /// The specification identity, <c>{Feature}/{Scenario}</c> -- the string the spec manifest,
    /// the ledger and <c>scenario_finished</c> key on. Absent for a scenario with no feature.
    /// </summary>
    public string? Id { get; set; }

    public string Title { get; set; } = "";
    public string? Feature { get; set; }
    public bool Succeeded { get; set; }

    /// <summary>
    /// <c>passed</c>, <c>failed</c>, <c>acceptance: failing</c> or <c>acceptance: passing</c>
    /// (bobcat#545). A failing Acceptance spec keeps <see cref="Succeeded"/> false and every step and
    /// cell; this is what says it does not fail the run.
    /// </summary>
    [System.ComponentModel.Description(
        "passed, failed, \"acceptance: failing\" or \"acceptance: passing\". A failing Acceptance specification keeps succeeded false and every step and cell, and does not fail the run.")]
    public string Verdict { get; set; } = "";

    /// <summary>
    /// <c>acceptance</c> for an Acceptance spec; absent for the default, Regression. Not called
    /// <c>lifecycle</c>, which is the timeline's (below).
    /// </summary>
    [System.ComponentModel.Description("\"acceptance\" for an Acceptance specification; absent for the default, Regression.")]
    public string? SpecLifecycle { get; set; }

    public JsonCountsOutput Counts { get; set; } = null!;

    /// <summary>True bracket wall clock, not the last step's end (issue #141).</summary>
    public long DurationMs { get; set; }

    /// <summary>
    /// What the steps themselves cost, top-level steps summed (issue #466). With
    /// <see cref="DurationMs"/> it says how much of the scenario no step owns. Projected lane only;
    /// absent when not measured, never zero-filled.
    /// </summary>
    public long? StepsMs { get; set; }

    /// <summary>
    /// Named lifecycle stop points — BeforeEach, the reset/scope bracket, teardown — on the
    /// same scenario clock as each step's startedAtMs, so a consumer can rank the time no
    /// step owns. Null when the results carried no timeline (a foreign worker, an older
    /// artifact): unmeasured is never zero-filled.
    /// </summary>
    public List<JsonTimelinePointOutput>? Lifecycle { get; set; }

    public List<JsonStepOutput> Steps { get; set; } = new();

    /// <summary>
    /// Why the scenario failed when no step accounts for it -- an assertion library that threw
    /// between steps, or a runner verdict whose cause Bobcat never saw. Absent when the steps
    /// already explain it.
    /// </summary>
    public string? ScenarioFailure { get; set; }

    /// <summary>Expected/actual pairs recovered from <see cref="ScenarioFailure"/>.</summary>
    public List<JsonCellOutput>? ScenarioFailureCells { get; set; }

    /// <summary>What <c>SpecLog.Write</c> recorded between steps (bobcat#547). Null when none is visible.</summary>
    public List<string>? Logs { get; set; }

    /// <summary>Scenario log lines past the cap that were not kept. Null when none were dropped.</summary>
    public int? SuppressedLogs { get; set; }

    [System.ComponentModel.Description("Attachments on the scenario rather than a step. Kept for a failed scenario, or any scenario in a verbose run.")]
    public List<JsonAttachmentOutput>? Attachments { get; set; }

    [System.ComponentModel.Description("Attachments past the per-scenario cap, counted rather than kept. Absent when none were dropped.")]
    public int? SuppressedAttachments { get; set; }
}

internal class JsonAttachmentOutput
{
    public string Name { get; set; } = "";
    public string MediaType { get; set; } = "";

    [System.ComponentModel.Description("Size in bytes.")]
    public long Size { get; set; }

    [System.ComponentModel.Description("The file, relative to the results directory. Absent when the attachment was not written there.")]
    public string? Path { get; set; }

    [System.ComponentModel.Description("Why the bytes were not kept, such as being over the size cap.")]
    public string? Omitted { get; set; }
}

internal class JsonTimelinePointOutput
{
    public string Name { get; set; } = "";
    public long StartedAtMs { get; set; }
    public long DurationMs { get; set; }
}

internal class JsonStepOutput
{
    public string StepId { get; set; } = "";
    public string Kind { get; set; } = "";

    /// <summary>The keyword as the specification wrote it (And/But included); absent when unknown.</summary>
    public string? Keyword { get; set; }

    /// <summary>Nesting under the narrative step it ran inside; absent at the top level.</summary>
    public int? Depth { get; set; }

    /// <summary>True for a marker comment, whose status only aggregates what ran inside it.</summary>
    public bool? Narrative { get; set; }

    /// <summary>True for a planned step the scenario never reached.</summary>
    public bool? NotRun { get; set; }

    public string Text { get; set; } = "";
    public string Status { get; set; } = "";
    public string? FailureLevel { get; set; }

    /// <summary>Offset from the scenario's announced start — the timeline half of the pair.</summary>
    public long StartedAtMs { get; set; }

    public long? DurationMs { get; set; }
    public string? Error { get; set; }
    public string? ExceptionType { get; set; }
    public List<string>? Logs { get; set; }

    /// <summary>Log lines past the per-step cap that were not kept (bobcat#547). Null when none were dropped.</summary>
    public int? SuppressedLogs { get; set; }

    public Dictionary<string, string>? Diagnostics { get; set; }

    /// <summary>Scalar comparison cells (return-value / out-param verification).</summary>
    public List<JsonCellOutput>? Cells { get; set; }

    public JsonSetVerificationOutput? SetVerification { get; set; }

    [System.ComponentModel.Description("What the step attached, and what a failure capture returned when it failed.")]
    public List<JsonAttachmentOutput>? Attachments { get; set; }
}

internal class JsonReportOutput
{
    public string Title { get; set; } = "";
    public string? ShortTitle { get; set; }

    /// <summary>Rows past the cap. Null rather than 0, so "nothing was dropped" is not a figure.</summary>
    public int? SuppressedRows { get; set; }

    public JsonSetVerificationOutput? Grid { get; set; }
}

internal class JsonSetVerificationOutput
{
    public List<string> Columns { get; set; } = new();
    public List<JsonSvRowOutput> Rows { get; set; } = new();
}

internal class JsonSvRowOutput
{
    public int Row { get; set; }
    public string Type { get; set; } = "";
    public List<JsonCellOutput>? Cells { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// AI-optimized structured cell: row/column coordinates plus typed expected/actual/note,
/// so an agent can jump straight to the failure without parsing a display string.
/// </summary>
internal class JsonCellOutput
{
    public string Column { get; set; } = "";
    public int? Row { get; set; }
    public string Status { get; set; } = "";
    public string? Expected { get; set; }
    public string? Actual { get; set; }
    public string? Note { get; set; }

    /// <summary>Plain value for input/echo cells that carry no typed comparison.</summary>
    public string? Value { get; set; }
}

internal class JsonCountsOutput
{
    public int Rights { get; set; }
    public int Wrongs { get; set; }
    public int Errors { get; set; }
    public bool Succeeded { get; set; }
}
