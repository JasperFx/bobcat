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
    private static readonly JsonSerializerOptions Options = new()
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
            Counts = CountsToJson(results.Counts),
            Features = results.Features.Select(RenderFeature).ToList()
        };

        return JsonSerializer.Serialize(output, Options);
    }

    public static string RenderScenario(SpecRender spec)
    {
        return JsonSerializer.Serialize(SpecToJson(spec), Options);
    }

    private static JsonFeatureOutput RenderFeature(FeatureResults feature)
    {
        return new JsonFeatureOutput
        {
            Title = feature.Title,
            Counts = CountsToJson(feature.Counts),
            HasRegressionFailure = feature.HasRegressionFailure,
            WasCatastrophic = feature.WasCatastrophic,
            Scenarios = feature.Scenarios.Select(s => SpecToJson(
                SpecRender.FromResults(s.Title, s.Results, feature.Title))).ToList()
        };
    }

    private static JsonScenarioOutput SpecToJson(SpecRender spec)
    {
        return new JsonScenarioOutput
        {
            Title = spec.Title,
            Feature = spec.FeatureTitle,
            Succeeded = spec.Succeeded,
            Counts = CountsToJson(spec.Counts),
            DurationMs = spec.DurationMs,
            Steps = spec.Steps.Select(StepToJson).ToList()
        };
    }

    private static JsonStepOutput StepToJson(StepRender step)
    {
        return new JsonStepOutput
        {
            StepId = step.StepId,
            Kind = step.Kind.ToString(),
            Text = step.StepText,
            Status = step.Status.ToString(),
            FailureLevel = step.FailureLevel != FailureLevel.None ? step.FailureLevel.ToString() : null,
            DurationMs = step.DurationMs > 0 ? step.DurationMs : null,
            Error = step.ErrorMessage,
            ExceptionType = step.ExceptionType,
            Logs = step.Logs.Count > 0 ? step.Logs : null,
            Diagnostics = step.Diagnostics.Count > 0 ? step.Diagnostics : null,
            Cells = step.Cells.Count > 0
                ? step.Cells.Select(c => CellToJson(c.Name, null, c.Status, c.Expected, c.Actual, c.Note, c.DisplayText)).ToList()
                : null,
            SetVerification = step.SetVerification != null ? SvToJson(step.SetVerification) : null
        };
    }

    private static JsonSetVerificationOutput SvToJson(SetVerificationRender sv)
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
                    ? r.Cells.Select(c => CellToJson(c.Column, rowIndex, c.Status, c.Expected, c.Actual, c.Note, c.DisplayText)).ToList()
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
    private static JsonCellOutput CellToJson(string column, int? row, ResultStatus status,
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

    private static JsonCountsOutput CountsToJson(Counts counts)
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
    public List<JsonFeatureOutput> Features { get; set; } = new();
}

internal class JsonFeatureOutput
{
    public string Title { get; set; } = "";
    public JsonCountsOutput Counts { get; set; } = null!;
    public bool HasRegressionFailure { get; set; }
    public bool WasCatastrophic { get; set; }
    public List<JsonScenarioOutput> Scenarios { get; set; } = new();
}

internal class JsonScenarioOutput
{
    public string Title { get; set; } = "";
    public string? Feature { get; set; }
    public bool Succeeded { get; set; }
    public JsonCountsOutput Counts { get; set; } = null!;
    public long DurationMs { get; set; }
    public List<JsonStepOutput> Steps { get; set; } = new();
}

internal class JsonStepOutput
{
    public string StepId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public string Status { get; set; } = "";
    public string? FailureLevel { get; set; }
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
