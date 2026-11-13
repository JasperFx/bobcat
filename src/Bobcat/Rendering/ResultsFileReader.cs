using System.Text.Json;
using Bobcat.Engine;

namespace Bobcat.Rendering;

/// <summary>
/// Reads a JSON results file (bobcat#540) back into the <see cref="SpecRender"/> model it was
/// written from, so a renderer that reads the file — the living-docs Markdown (bobcat#553) — renders
/// the same model the console, the JSON and the plain text render from, rather than a second one.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the file cannot give back.</b> The file carries no exception object (only its type name
/// and message), no value spans (a sentence renders as prose and its cells on their own lines), no
/// absolute attachment paths (only the path relative to the results directory) and no comparison
/// word on a cell, so a non-equality cell reads back in the equality shape. Every renderer over a
/// read model shows what the file has and nothing it has not.
/// </para>
/// <para>
/// <b>An older file still reads.</b> Properties the file lacks take their defaults and properties
/// this reader does not know are ignored, so a results file from an earlier release renders with
/// what it carries. A file that is not a results file at all (no <c>scenarios</c> array) is refused
/// with <see cref="InvalidDataException"/>, and one that is not JSON with <see cref="JsonException"/>.
/// </para>
/// </remarks>
public static class ResultsFileReader
{
    /// <summary>Read a results file from its text.</summary>
    /// <exception cref="JsonException">The text is not JSON.</exception>
    /// <exception cref="InvalidDataException">The JSON is not a results file.</exception>
    public static ResultsFileContents Read(string json, string? path = null)
    {
        using (var probe = JsonDocument.Parse(json))
        {
            // The shape check before the typed read: a missing list would otherwise deserialize as an
            // empty one, and a file that is not a results file would read as a run of nothing.
            if (probe.RootElement.ValueKind != JsonValueKind.Object
                || !probe.RootElement.TryGetProperty("scenarios", out var scenarios)
                || scenarios.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"{path ?? "The file"} is not a Bobcat results file: it has no 'scenarios' array. "
                    + "Results files are written by BOBCAT_RESULTS=<dir> or `bobcat run --results <dir>`.");
            }
        }

        var document = JsonSerializer.Deserialize<JsonResultsOutput>(json, JsonRenderer.options)
                       ?? throw new InvalidDataException("The results file is empty.");

        var run = document.Run ?? new JsonRunOutput();

        return new ResultsFileContents
        {
            Path = path,
            SchemaVersion = document.SchemaVersion ?? "",
            RunId = run.RunId,
            Suite = run.Suite ?? "",
            Mode = run.Mode ?? "",
            Repository = run.Repository ?? "",
            Branch = run.Branch,
            Tag = run.Tag,
            Command = run.Command,
            Order = run.Order,
            Seed = run.Seed,
            Shard = run.Shard,
            StartedAt = run.StartedAt,
            FinishedAt = run.FinishedAt,
            DurationMs = run.DurationMs,
            Scenarios = document.Scenarios.Select(scenarioOf).ToList()
        };
    }

    /// <summary>Read the results file at <paramref name="path"/>.</summary>
    public static ResultsFileContents ReadFile(string path)
        => Read(File.ReadAllText(path), System.IO.Path.GetFullPath(path));

    /// <summary>
    /// The specification identity a scenario is keyed on everywhere: <c>{Feature}/{Scenario}</c>, or
    /// the title alone for a scenario with no feature. The same string the file's <c>id</c> carries.
    /// </summary>
    public static string IdentityOf(SpecRender spec)
        => spec.FeatureTitle is { Length: > 0 } feature ? $"{feature}/{spec.Title}" : spec.Title;

    private static SpecRender scenarioOf(JsonScenarioOutput scenario)
    {
        var verdict = scenario.Verdict ?? "";
        var acceptance = scenario.SpecLifecycle == "acceptance" || verdict.StartsWith("acceptance", StringComparison.Ordinal);
        var acceptanceFailing = verdict == SpecLifecycle.FailingVerdict;
        var succeeded = acceptanceFailing ? false : verdict == "acceptance: passing" || scenario.Succeeded;

        var counts = scenario.Counts is { } c ? new Counts(c.Rights, c.Wrongs, c.Errors) : new Counts();

        return new SpecRender
        {
            Title = scenario.Title ?? "",
            FeatureTitle = scenario.Feature,
            Succeeded = succeeded,
            Lifecycle = acceptance ? Lifecycle.Acceptance : Lifecycle.Regression,
            AcceptanceFailing = acceptanceFailing,
            Counts = counts,
            DurationMs = scenario.DurationMs,
            StepsMs = scenario.StepsMs,
            Steps = (scenario.Steps ?? []).Select(stepOf).ToList(),
            ScenarioFailure = scenario.ScenarioFailure,
            ScenarioFailureCells = (scenario.ScenarioFailureCells ?? []).Select(cellOf).ToList(),
            Reports = (scenario.Reports ?? []).Select(reportOf).ToList(),
            Logs = scenario.Logs?.ToList() ?? [],
            SuppressedLogs = scenario.SuppressedLogs ?? 0,
            Attachments = (scenario.Attachments ?? []).Select(attachmentOf).ToList(),
            SuppressedAttachments = scenario.SuppressedAttachments ?? 0
        };
    }

    private static StepRender stepOf(JsonStepOutput step)
    {
        var status = parse(step.Status, ResultStatus.ok);

        return new StepRender
        {
            StepId = step.StepId ?? "",
            Kind = parse(step.Kind, StepKind.Then),
            Keyword = step.Keyword,
            Depth = step.Depth ?? 0,
            IsNarrative = step.Narrative == true,
            NotRun = step.NotRun == true,
            StepText = step.Text ?? "",
            Status = status,
            FailureLevel = parse(step.FailureLevel, FailureLevel.None),
            StartedAtMs = step.StartedAtMs,
            DurationMs = step.DurationMs ?? 0,
            ErrorMessage = step.Error,
            ExceptionType = step.ExceptionType,
            Logs = step.Logs?.ToList() ?? [],
            SuppressedLogs = step.SuppressedLogs ?? 0,
            Diagnostics = step.Diagnostics is null ? new Dictionary<string, string>() : new Dictionary<string, string>(step.Diagnostics),
            Cells = (step.Cells ?? []).Select(c => cellOf(c, step.SetVerification)).ToList(),
            SetVerification = step.SetVerification is null ? null : gridOf(step.SetVerification),
            Attachments = (step.Attachments ?? []).Select(attachmentOf).ToList()
        };
    }

    /// <summary>
    /// A cell, with its display text derived by <see cref="CellResult"/> itself — the one phrasing of
    /// <c>expected 'x', got 'y'</c> — so a read cell cannot say anything a written one did not.
    /// </summary>
    private static CellResult cellResultOf(JsonCellOutput cell)
    {
        var status = parse(cell.Status, ResultStatus.ok);
        var name = cell.Column ?? "";
        var rowIndex = cell.Row ?? -1;

        // A value cell (bobcat#396): nothing was judged, and its text is the literal. The legacy
        // constructor keeps it verbatim, which is how it was built.
        if (cell.Expected is null && cell.Actual is null && cell.Value is not null)
        {
            return new CellResult(name, status, cell.Value) { Note = cell.Note, RowIndex = rowIndex };
        }

        return new CellResult(name, status) { Expected = cell.Expected, Actual = cell.Actual, Note = cell.Note, RowIndex = rowIndex };
    }

    private static CellRender cellOf(JsonCellOutput cell) => CellRender.From(cellResultOf(cell));

    /// <summary>
    /// A step's cell. The file writes a grid cell's row since bobcat#553; before that a step's
    /// cells carried none, so for an older file a cell named for one of the grid's columns, or a row
    /// marker, is taken as the grid's — otherwise every renderer would list the grid a second time.
    /// </summary>
    private static CellRender cellOf(JsonCellOutput cell, JsonSetVerificationOutput? grid)
    {
        if (cell.Row is null && grid is not null && cell.Column is { } column
            && (grid.Columns?.Contains(column) == true || SetVerificationRender.BelongsToARow(0, column)))
        {
            return cellOf(new JsonCellOutput
            {
                Column = cell.Column, Row = 0, Status = cell.Status, Expected = cell.Expected, Actual = cell.Actual, Note = cell.Note, Value = cell.Value
            });
        }

        return cellOf(cell);
    }

    private static SetVerificationRender gridOf(JsonSetVerificationOutput grid)
    {
        var columns = grid.Columns?.ToList() ?? [];
        var rows = new List<SetVerificationRowRender>();

        foreach (var row in grid.Rows ?? [])
        {
            var cells = (row.Cells ?? []).Select(cellResultOf).ToList();
            var rendered = new SetVerificationRowRender
            {
                RowType = parse(row.Type, SetVerificationRowType.Matched),
                Description = row.Description
            };

            // In column order, as the grid was written: a cell per column, and none at all for an
            // absent row an older producer described without values.
            if (cells.Count > 0)
            {
                foreach (var column in columns)
                {
                    var cell = cells.LastOrDefault(x => x.Name == column);
                    rendered.Cells.Add(new SetVerificationCellRender
                    {
                        Column = column,
                        Status = cell?.Status ?? ResultStatus.ok,
                        DisplayText = cell?.DisplayText ?? "",
                        Expected = cell?.Expected,
                        Actual = cell?.Actual,
                        Note = cell?.Note
                    });
                }
            }

            rows.Add(rendered);
        }

        return new SetVerificationRender { Columns = columns, Rows = rows };
    }

    private static ReportRender reportOf(JsonReportOutput report)
        => new()
        {
            Title = report.Title ?? "",
            ShortTitle = report.ShortTitle,
            SuppressedRows = report.SuppressedRows ?? 0,
            Grid = report.Grid is null ? new SetVerificationRender() : gridOf(report.Grid)
        };

    private static AttachmentRender attachmentOf(JsonAttachmentOutput attachment)
        => new()
        {
            Name = attachment.Name ?? "",
            MediaType = attachment.MediaType ?? "application/octet-stream",
            Size = attachment.Size,
            Path = attachment.Path,
            Omitted = attachment.Omitted
        };

    private static T parse<T>(string? text, T fallback) where T : struct, Enum
        => text is { Length: > 0 } && Enum.TryParse<T>(text, ignoreCase: true, out var value) ? value : fallback;
}

/// <summary>One results file, read back: the run it describes and every scenario as a <see cref="SpecRender"/>.</summary>
public sealed class ResultsFileContents
{
    /// <summary>The file's full path, when it was read from one.</summary>
    public string? Path { get; init; }

    public string SchemaVersion { get; init; } = "";
    public Guid RunId { get; init; }
    public string Suite { get; init; } = "";
    public string Mode { get; init; } = "";
    public string Repository { get; init; } = "";
    public string? Branch { get; init; }
    public string? Tag { get; init; }
    public string? Command { get; init; }

    /// <summary><c>random</c> when the run was <c>bobcat run --order random</c> (bobcat#578); null for a plain run or an older file.</summary>
    public string? Order { get; init; }

    /// <summary>The seed a random <see cref="Order"/> ran with; null otherwise.</summary>
    public int? Seed { get; init; }

    /// <summary>The leg (<c>i/n</c>) when the run was one shard of a suite, <c>bobcat run --shard</c> (bobcat#576); null otherwise.</summary>
    public string? Shard { get; init; }

    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public long DurationMs { get; init; }

    /// <summary>Every scenario in the file, in the file's order (feature, then title).</summary>
    public IReadOnlyList<SpecRender> Scenarios { get; init; } = [];
}
