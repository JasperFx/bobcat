using System.Text;
using Bobcat.Console.Runs;
using Wolverine.Http;

namespace Bobcat.Console;

/// <summary>
/// One run as an external consumer sees it. This is a PUBLIC wire contract, not just the
/// dashboard's list model: an outside tool correlating its own work to a suite (by
/// <c>BOBCAT_RUN_TAG</c>) has no other way in, so the summary carries enough to render a
/// verdict without a second call — outcome counts and scenario progress included.
/// </summary>
public record RunSummary(
    Guid RunId,
    string Suite,
    string Repository,
    string? Branch,
    string Mode,
    bool Finished,
    bool Orphaned,
    int? ExitCode,
    int Scenarios,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt)
{
    /// <summary>The opaque BOBCAT_RUN_TAG this run was launched with, if any.</summary>
    public string? Tag { get; init; }

    /// <summary>Declared scenario total, when the publisher knew it up front.</summary>
    public int? TotalScenarios { get; init; }

    /// <summary>Scenarios that have reported an outcome — live progress for a running suite.</summary>
    public int ScenariosFinished { get; init; }

    // Final counts, present once RunFinished arrives. Indeterminate stays separate from Failed
    // for the same reason exit 2 is not exit 1 — "we don't know" is not a red build.
    public int? Passed { get; init; }
    public int? Failed { get; init; }
    public int? PassedOnRetry { get; init; }
    public int? Indeterminate { get; init; }
}

/// <summary>
/// One run in full: the summary plus every scenario's current state. Same wire-contract status
/// as <see cref="RunSummary"/> — the per-scenario view is how an outside consumer (or the
/// viewer's own end-to-end suite, <c>Bobcat.Console.Specs</c>) verifies an outcome without
/// replaying the NDJSON archive.
/// </summary>
public record RunDetail(RunSummary Run, ScenarioResult[] Scenarios);

/// <summary>
/// One scenario as the registry currently sees it. <c>Attempt</c> is the attempt running or
/// last run (1-based); <c>Attempts</c> is the total from the terminal event, corrected upward
/// by what the monitor watched start. <c>Outcome</c> mirrors RunOutcome and is null while
/// running — a <c>PassOnRetry</c> is never collapsed into a clean pass here either.
/// </summary>
public record ScenarioResult(
    string Uid,
    string Feature,
    string Scenario,
    string? Outcome,
    int Attempt,
    int? Attempts,
    long? DurationMs,
    string? ErrorMessage,
    string[] RetryReasons,
    StepResult[] Steps);

public record StepResult(string StepId, string Kind, string Text, string Status, long? DurationMs, string? ErrorMessage);

public static class RunEndpoints
{
    /// <summary>
    /// Every known run, newest first. <paramref name="tag"/> filters to runs launched with a
    /// matching <c>BOBCAT_RUN_TAG</c> — the correlation hook for an external tool that wants
    /// its own runs and not the whole box's.
    /// </summary>
    [WolverineGet("/api/runs")]
    public static RunSummary[] All(MonitorRunRegistry registry, string? tag = null)
        => registry.ReadAll(all => all
            .Where(r => tag is null || r.Tag == tag)
            .OrderByDescending(r => r.StartedAt ?? DateTimeOffset.MinValue)
            .Select(summarize)
            .ToArray());

    /// <summary>
    /// One run with its per-scenario results, or 404 once it has been ejected. Read under the
    /// registry's gate like the exports are — a run mutates under live ingestion.
    /// </summary>
    [WolverineGet("/api/runs/{runId}")]
    public static IResult Find(Guid runId, MonitorRunRegistry registry)
    {
        var detail = registry.Read(runId, run => new RunDetail(
            summarize(run),
            run.Scenarios
                .OrderBy(s => s.Feature).ThenBy(s => s.Scenario)
                .Select(s => new ScenarioResult(
                    s.Uid, s.Feature, s.Scenario, s.Outcome, s.Attempt, s.Attempts, s.DurationMs,
                    s.ErrorMessage,
                    s.RetryReasons.ToArray(),
                    s.Steps
                        .Select(step => new StepResult(
                            step.StepId, step.Kind, step.Text, step.Status, step.DurationMs, step.ErrorMessage))
                        .ToArray()))
                .ToArray()));

        return detail == null ? Results.NotFound() : Results.Ok(detail);
    }

    private static RunSummary summarize(RunProjection r)
        => new(
            r.RunId, r.Suite, r.Repository, r.Branch, r.Mode,
            r.Finished, r.Orphaned, r.ExitCode, r.Scenarios.Count, r.StartedAt, r.FinishedAt)
        {
            Tag = r.Tag,
            TotalScenarios = r.TotalScenarios,
            ScenariosFinished = r.Scenarios.Count(s => s.Outcome != null),
            Passed = r.Passed,
            Failed = r.Failed,
            PassedOnRetry = r.PassedOnRetry,
            Indeterminate = r.Indeterminate
        };

    /// <summary>
    /// The eject download: ctrf (primary), junit (CI compatibility floor), or ndjson (the raw
    /// archived event stream, replayable). Exporting a still-running run is allowed — the
    /// formats mark unfinished scenarios pending/skipped rather than guessing.
    /// </summary>
    [WolverineGet("/api/runs/{runId}/export")]
    public static IResult Export(Guid runId, string? format, MonitorRunRegistry registry)
    {
        var run = registry.Find(runId);

        switch (format?.ToLowerInvariant())
        {
            case "ndjson":
            {
                var archive = registry.ReadArchive(runId);
                return archive == null
                    ? Results.NotFound()
                    : Results.File(archive, "application/x-ndjson", $"{fileStem(run, runId)}.ndjson");
            }

            case "junit":
            {
                // Rendered under the registry's gate — the projection mutates during live
                // ingestion, and a torn scenario enumeration must not corrupt an export.
                var xml = registry.Read(runId, JUnitExport.Render);
                return xml == null
                    ? Results.NotFound()
                    : file(xml, "application/xml", $"{fileStem(run, runId)}.junit.xml");
            }

            case null or "ctrf":
            {
                var json = registry.Read(runId, CtrfExport.Render);
                return json == null
                    ? Results.NotFound()
                    : file(json, "application/json", $"{fileStem(run, runId)}.ctrf.json");
            }

            default:
                return Results.BadRequest($"unknown format '{format}' — expected ctrf, junit, or ndjson");
        }
    }

    /// <summary>Eject from the dashboard/registry. The NDJSON archive stays on disk.</summary>
    /// <remarks>
    /// [NotBody] matters: on a bodyless DELETE, Wolverine's binding otherwise assumes the first
    /// complex parameter IS the request body and 400s on the empty payload. GETs never hit this
    /// (no body to bind) and the ingest POST doesn't either (IngestBatch legitimately claims the
    /// body slot first) — found live when the UI's first real eject came back 400.
    /// </remarks>
    [WolverineDelete("/api/runs/{runId}")]
    public static IResult Eject(Guid runId, [NotBody] MonitorRunRegistry registry)
        => registry.Remove(runId) ? Results.NoContent() : Results.NotFound();

    private static IResult file(string content, string contentType, string fileName)
        => Results.File(Encoding.UTF8.GetBytes(content), contentType, fileName);

    private static string fileStem(RunProjection? run, Guid runId)
    {
        var suite = run?.Suite ?? "run";
        var safe = new string(suite.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (safe.Length == 0) safe = "run";
        return $"{safe}-{runId.ToString()[..8]}";
    }
}
