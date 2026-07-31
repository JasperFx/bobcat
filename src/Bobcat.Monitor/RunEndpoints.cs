using System.Text;
using Bobcat.Monitor.Runs;
using Wolverine.Http;

namespace Bobcat.Monitor;

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
    DateTimeOffset? FinishedAt);

public static class RunEndpoints
{
    [WolverineGet("/api/runs")]
    public static RunSummary[] All(MonitorRunRegistry registry)
        => registry.All()
            .OrderByDescending(r => r.StartedAt ?? DateTimeOffset.MinValue)
            .Select(r => new RunSummary(
                r.RunId, r.Suite, r.Repository, r.Branch, r.Mode,
                r.Finished, r.Orphaned, r.ExitCode, r.Scenarios.Count, r.StartedAt, r.FinishedAt))
            .ToArray();

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
                return run == null
                    ? Results.NotFound()
                    : file(JUnitExport.Render(run), "application/xml", $"{fileStem(run, runId)}.junit.xml");

            case null or "ctrf":
                return run == null
                    ? Results.NotFound()
                    : file(CtrfExport.Render(run), "application/json", $"{fileStem(run, runId)}.ctrf.json");

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
