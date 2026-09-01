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
public record RunDetail(RunSummary Run, ScenarioResult[] Scenarios)
{
    // The supervisor's topology (issue #84). All three are empty for an in-process run —
    // nothing is inferred for a run that never announced a lane. Additive, so a consumer
    // written against the two-member shape keeps deserializing.

    /// <summary>Supervisor lanes in lane order, each with what it was handed and what it is running now.</summary>
    public LaneResult[] Lanes { get; init; } = [];

    /// <summary>Resources the supervisor recycled before a retry, in order.</summary>
    public RecycleResult[] Recycles { get; init; } = [];

    /// <summary>Worker processes that died, with lane, exit code and last standard error.</summary>
    public WorkerFaultResult[] WorkerFaults { get; init; } = [];

    /// <summary>Tests the supervisor reported as stalled (issue #145), in detection order. Additive.</summary>
    public StallResult[] Stalls { get; init; } = [];

    /// <summary>
    /// The supervisor's latest progress heartbeat (issue #148), when the run publishes one —
    /// the only live progress a foreign-framework worker gives. Null otherwise. Additive.
    /// </summary>
    public RunProgressResult? Progress { get; init; }
}

/// <summary>
/// One supervisor lane. <c>Status</c> is running / finished / crashed. <c>Uids</c> is what the
/// lane was handed on its latest pass (a same-process retry hands the lane only the retried
/// tests, so this is "working through now", not everything it ever ran); <c>Running</c> is the
/// subset of those uids whose scenario has no outcome yet; <c>Passes</c> counts how many times the
/// lane was handed work. <c>Outcomes</c> is how many results the worker reported when it finished.
/// </summary>
public record LaneResult(
    int Lane,
    string Status,
    int Passes,
    string[] Uids,
    string[] Running,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int? Outcomes)
{
    /// <summary>The OS pid of the lane's worker process (issue #146), when known. Additive.</summary>
    public int? ProcessId { get; init; }
}

public record RecycleResult(string Resource, DateTimeOffset At);

/// <summary>A stalled test (issue #145): in flight past its threshold, named.</summary>
public record StallResult(string Uid, string DisplayName, long InFlightMs, int? Lane, DateTimeOffset At);

/// <summary>The supervisor's latest progress heartbeat (issue #148); see RunProgressProjection.</summary>
public record RunProgressResult(
    long ElapsedMs,
    int Completed,
    int Total,
    int InFlight,
    string? LongestRunningUid,
    string? LongestRunningDisplayName,
    long? LongestRunningMs,
    long? PeakWorkerRssBytes,
    DateTimeOffset At);

/// <summary>
/// A dead worker. <c>Lane</c> is null for a one-test isolated or recycled process; <c>Fault</c> is
/// the sentence the supervisor's own report collects, so report and viewer agree.
/// </summary>
public record WorkerFaultResult(int? Lane, string Fault, int? ExitCode, string? StandardError, DateTimeOffset At);

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
    StepResult[] Steps)
{
    /// <summary>
    /// The run evidence (issue #107): CLR types the scenario observably touched — commands
    /// dispatched, events emitted, aggregates arranged, messages sent, read models loaded — in
    /// first-touch order. <c>FullName</c> joins against a design-time
    /// <c>SpecificationDescriptor.ResolvedTypes</c>; <c>Uid</c> is the identity both sides key
    /// on. Empty when the publisher recorded nothing. Additive.
    /// </summary>
    public Contracts.TouchedType[] TouchedTypes { get; init; } = [];

    /// <summary>When the scenario finished — evidence is observed, and this is its stamp. Additive.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>
    /// The worker framework's own word for the verdict — Passed / Failed / Error / Skipped /
    /// Timeout / Cancelled — for a test the supervisor forwarded rather than a Bobcat worker
    /// published (issue #195). Null for a Bobcat scenario, whose vocabulary is
    /// <see cref="Outcome"/>'s. Additive.
    /// </summary>
    public string? State { get; init; }
}

public record StepResult(string StepId, string Kind, string Text, string Status, long? DurationMs, string? ErrorMessage);

/// <summary>
/// What a bulk eject took (issue #197). The ids are returned, not just the count, so a caller
/// drops exactly those cards instead of guessing which of its own the server agreed with — a
/// live run the filter matched is deliberately not among them.
/// </summary>
public record EjectedRuns(int Count, Guid[] RunIds);

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
                        .ToArray())
                {
                    TouchedTypes = s.TouchedTypes.ToArray(),
                    FinishedAt = s.FinishedAt,
                    State = s.State
                })
                .ToArray())
        {
            Lanes = run.Lanes
                .Select(l => new LaneResult(
                    l.Lane, l.Status, l.Passes, l.Uids.ToArray(),
                    run.RunningIn(l).Select(s => s.Uid).ToArray(),
                    l.StartedAt, l.FinishedAt, l.Outcomes) { ProcessId = l.ProcessId })
                .ToArray(),
            Recycles = run.Recycles.Select(r => new RecycleResult(r.Resource, r.At)).ToArray(),
            WorkerFaults = run.WorkerFaults
                .Select(f => new WorkerFaultResult(f.Lane, f.Fault, f.ExitCode, f.StandardError, f.At))
                .ToArray(),
            Stalls = run.Stalls
                .Select(s => new StallResult(s.Uid, s.DisplayName, s.InFlightMs, s.Lane, s.At))
                .ToArray(),
            Progress = run.Progress is { } progress
                ? new RunProgressResult(
                    progress.ElapsedMs, progress.Completed, progress.Total, progress.InFlight,
                    progress.LongestRunningUid, progress.LongestRunningDisplayName,
                    progress.LongestRunningMs, progress.PeakWorkerRssBytes, progress.At)
                : null
        });

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

    /// <summary>
    /// Bulk eject (issue #197): clear the board in one action rather than one card at a time.
    /// With no parameters it takes every finished or orphaned run; <paramref name="olderThan"/>
    /// narrows to runs that started strictly before an instant — the run the user anchored on
    /// survives its own "older than this" — and <paramref name="exceptRunId"/>
    /// spares one — the three verbs a browser's tab menu already taught everyone (close all,
    /// close to the right, close others). Both narrow the same set, so they compose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is eject, not delete: every archive moves to <c>ejected/</c> exactly as the per-run
    /// button does and stays on disk under the age policy. That is what makes a one-click "take
    /// all 43" a reasonable control to offer at all, and it is why the UI says so on the button.
    /// </para>
    /// <para>
    /// A live run is never taken, whatever the filter matched — see
    /// <c>MonitorRunRegistry.RemoveWhere</c>: its publisher recreates the entry with the next
    /// event, so ejecting it would buy a card that reappears and a count that lied.
    /// </para>
    /// <para>
    /// <c>[NotBody]</c> for the same hard-won reason the per-run delete carries it: on a
    /// bodyless DELETE, Wolverine's binding otherwise claims the first complex parameter as the
    /// request body and 400s on the empty payload.
    /// </para>
    /// </remarks>
    [WolverineDelete("/api/runs")]
    public static EjectedRuns EjectMany(
        [NotBody] MonitorRunRegistry registry,
        DateTimeOffset? olderThan = null,
        Guid? exceptRunId = null)
    {
        var ejected = registry.RemoveWhere(run =>
            run.RunId != exceptRunId &&
            (olderThan is not { } cutoff || (run.StartedAt ?? DateTimeOffset.MinValue) < cutoff));

        return new EjectedRuns(ejected.Count, ejected.ToArray());
    }

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
