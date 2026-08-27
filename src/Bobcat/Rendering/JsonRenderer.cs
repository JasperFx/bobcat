using System.Text.Json;
using System.Text.Json.Serialization;
using Bobcat.Engine;
using Bobcat.Runtime;

namespace Bobcat.Rendering;

/// <summary>
/// Renders suite/feature/scenario results as structured JSON.
/// Designed for AI consumption via MCP diagnose_failing_spec tool.
/// </summary>
public static class JsonRenderer
{
    private static readonly JsonSerializerOptions options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string RenderSuite(SuiteResults results)
    {
        var output = new JsonSuiteOutput
        {
            ExitCode = results.ExitCode,
            Counts = countsToJson(results.Counts),
            PreflightFailure = results.PreflightFailure,
            CatastrophicFailure = results.CatastrophicFailure,
            NotRun = results.NotRun.Count > 0
                ? results.NotRun.Select(n => new JsonNotRunOutput
                {
                    Feature = n.FeatureTitle, Title = n.Title, Reason = n.Reason
                }).ToList()
                : null,
            Features = results.Features.Select(renderFeature).ToList(),
            Timing = timingToJson(SuiteTiming.For(results))
        };

        return JsonSerializer.Serialize(output, options);
    }

    /// <summary>
    /// The full #142 figures — every scenario, step, lifecycle point and gap, uncapped: the
    /// console renders a summary, this is the record. Null when nothing was measured and
    /// nothing is flagged, so an older artifact reads as absent rather than zero.
    /// </summary>
    private static JsonTimingOutput? timingToJson(SuiteTiming timing)
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
                LifecycleMs = (long)s.Lifecycle.TotalMilliseconds,
                UnownedMs = (long)s.Unowned.TotalMilliseconds
            }).ToList(),
            Steps = timing.Steps.Select(costToJson).ToList(),
            Lifecycle = timing.Lifecycle.Select(costToJson).ToList(),
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

    private static JsonFeatureOutput renderFeature(FeatureResults feature)
    {
        return new JsonFeatureOutput
        {
            Title = feature.Title,
            Counts = countsToJson(feature.Counts),
            HasRegressionFailure = feature.HasRegressionFailure,
            WasCatastrophic = feature.WasCatastrophic,
            LifecycleFailure = feature.LifecycleFailure,
            Scenarios = feature.Scenarios.Select(s => specToJson(
                SpecRender.FromResults(s.Title, s.Results, feature.Title))).ToList()
        };
    }

    private static JsonScenarioOutput specToJson(SpecRender spec)
    {
        return new JsonScenarioOutput
        {
            Title = spec.Title,
            Feature = spec.FeatureTitle,
            Succeeded = spec.Succeeded,
            Counts = countsToJson(spec.Counts),
            DurationMs = spec.DurationMs,
            Lifecycle = spec.Timeline.Count > 0
                ? spec.Timeline.Select(p => new JsonTimelinePointOutput
                {
                    Name = p.Name,
                    StartedAtMs = p.StartedAtMs,
                    DurationMs = p.DurationMs
                }).ToList()
                : null,
            Steps = spec.Steps.Select(stepToJson).ToList()
        };
    }

    private static JsonStepOutput stepToJson(StepRender step)
    {
        return new JsonStepOutput
        {
            StepId = step.StepId,
            Kind = step.Kind.ToString(),
            Text = step.StepText,
            Status = step.Status.ToString(),
            FailureLevel = step.FailureLevel != FailureLevel.None ? step.FailureLevel.ToString() : null,
            StartedAtMs = step.StartedAtMs,
            DurationMs = step.DurationMs > 0 ? step.DurationMs : null,
            Error = step.ErrorMessage,
            ExceptionType = step.ExceptionType,
            Logs = step.Logs.Count > 0 ? step.Logs : null,
            Diagnostics = step.Diagnostics.Count > 0 ? step.Diagnostics : null,
            Cells = step.Cells.Count > 0
                ? step.Cells.Select(c => cellToJson(c.Name, null, c.Status, c.Expected, c.Actual, c.Note, c.DisplayText)).ToList()
                : null,
            SetVerification = step.SetVerification != null ? svToJson(step.SetVerification) : null
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
internal class JsonSuiteOutput
{
    public int ExitCode { get; set; }
    public JsonCountsOutput Counts { get; set; } = null!;

    /// <summary>Why the harness stopped the run, when it did. Null on a run that got going.</summary>
    public string? PreflightFailure { get; set; }
    public string? CatastrophicFailure { get; set; }

    /// <summary>Scenarios the run planned and never executed, each with the reason.</summary>
    public List<JsonNotRunOutput>? NotRun { get; set; }

    public List<JsonFeatureOutput> Features { get; set; } = new();

    /// <summary>Where the run spent its time (issue #142). Null when nothing was measured.</summary>
    public JsonTimingOutput? Timing { get; set; }
}

internal class JsonTimingOutput
{
    public long MeasuredMs { get; set; }

    /// <summary>Scenarios with no wall clock — the figures above are a floor, never zero-filled.</summary>
    public int? Unmeasured { get; set; }

    /// <summary>Every measured scenario, slowest first.</summary>
    public List<JsonScenarioTimingOutput> Scenarios { get; set; } = new();

    /// <summary>Per normalized step text across the suite, costliest first.</summary>
    public List<JsonStepCostOutput> Steps { get; set; } = new();

    /// <summary>Per lifecycle point across the suite, costliest first.</summary>
    public List<JsonStepCostOutput> Lifecycle { get; set; } = new();

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
    public long LifecycleMs { get; set; }
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

internal class JsonNotRunOutput
{
    public string Feature { get; set; } = "";
    public string Title { get; set; } = "";
    public string Reason { get; set; } = "";
}

internal class JsonFeatureOutput
{
    public string Title { get; set; } = "";
    public JsonCountsOutput Counts { get; set; } = null!;
    public bool HasRegressionFailure { get; set; }
    public bool WasCatastrophic { get; set; }
    public string? LifecycleFailure { get; set; }
    public List<JsonScenarioOutput> Scenarios { get; set; } = new();
}

internal class JsonScenarioOutput
{
    public string Title { get; set; } = "";
    public string? Feature { get; set; }
    public bool Succeeded { get; set; }
    public JsonCountsOutput Counts { get; set; } = null!;

    /// <summary>True bracket wall clock, not the last step's end (issue #141).</summary>
    public long DurationMs { get; set; }

    /// <summary>
    /// Named lifecycle stop points — BeforeEach, the reset/scope bracket, teardown — on the
    /// same scenario clock as each step's startedAtMs, so a consumer can rank the time no
    /// step owns. Null when the results carried no timeline (a foreign worker, an older
    /// artifact): unmeasured is never zero-filled.
    /// </summary>
    public List<JsonTimelinePointOutput>? Lifecycle { get; set; }

    public List<JsonStepOutput> Steps { get; set; } = new();
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
    public string Text { get; set; } = "";
    public string Status { get; set; } = "";
    public string? FailureLevel { get; set; }

    /// <summary>Offset from the scenario's announced start — the timeline half of the pair.</summary>
    public long StartedAtMs { get; set; }

    public long? DurationMs { get; set; }
    public string? Error { get; set; }
    public string? ExceptionType { get; set; }
    public List<string>? Logs { get; set; }
    public Dictionary<string, string>? Diagnostics { get; set; }

    /// <summary>Scalar comparison cells (return-value / out-param verification).</summary>
    public List<JsonCellOutput>? Cells { get; set; }

    public JsonSetVerificationOutput? SetVerification { get; set; }
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
