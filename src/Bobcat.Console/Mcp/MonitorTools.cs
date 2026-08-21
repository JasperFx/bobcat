using System.ComponentModel;
using System.Text.Json;
using Bobcat.Console.Runs;
using ModelContextProtocol.Server;

namespace Bobcat.Console.Mcp;

/// <summary>
/// MCP tools mirroring the dashboard's queries, following the CritterWatch *.Mcp shape:
/// static [McpServerTool] methods returning camelCase JSON strings, dependencies injected.
/// The audience is an AI agent that just kicked off a test suite on this box and wants to
/// reason about progress — which is why <c>await_run_completion</c> exists: block until the
/// suite settles instead of polling in a loop.
///
/// All reads go through the registry's locked Read/ReadAll so a projection mutating under
/// live ingestion can never hand a tool a torn scenario collection.
/// </summary>
[McpServerToolType]
public class MonitorTools
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static string toJson(object value) => JsonSerializer.Serialize(value, jsonOptions);

    private static string error(string message) => toJson(new { error = message });

    [McpServerTool(Name = "list_runs")]
    [Description(
        "List every Bobcat test run this monitor knows about — running, finished, and " +
        "orphaned (rehydrated from an archive after its publisher died). Newest first. " +
        "Use the runId with the other tools.")]
    public static string ListRuns(MonitorRunRegistry registry)
        => registry.ReadAll(runs => toJson(runs
            .OrderByDescending(r => r.StartedAt ?? DateTimeOffset.MinValue)
            .Select(summarize)
            .ToArray()));

    [McpServerTool(Name = "run_status")]
    [Description(
        "Progress detail for one run: per-scenario outcomes, the currently executing " +
        "scenario's live steps, and retry activity. Omit runId for the most recent run.")]
    public static string RunStatus(
        MonitorRunRegistry registry,
        [Description("Run id from list_runs; omit for the most recent run.")] string? runId = null)
    {
        var resolved = resolve(registry, runId, out var problem);
        if (resolved == null) return problem!;

        return registry.Read(resolved.Value, run => toJson(new
        {
            run = summarize(run),
            scenarios = run.Scenarios
                .OrderBy(s => s.Feature).ThenBy(s => s.Scenario)
                .Select(s => new
                {
                    uid = s.Uid,
                    status = s.Outcome ?? "running",
                    attempt = s.Attempt,
                    attempts = s.Attempts,
                    durationMs = s.DurationMs,
                    errorMessage = s.ErrorMessage,
                    retryReasons = s.RetryReasons.Count > 0 ? s.RetryReasons : null,
                    // Live step detail only for the scenario still executing — finished
                    // scenarios summarize to their outcome.
                    steps = s.Outcome == null
                        ? s.Steps.Select(step => new
                        {
                            name = $"{step.Kind} {step.Text}",
                            status = step.Status,
                            durationMs = step.DurationMs
                        }).ToArray()
                        : null
                })
                .ToArray()
        })) ?? error($"run {resolved} disappeared while reading");
    }

    [McpServerTool(Name = "failing_tests")]
    [Description(
        "Every failed or aborted scenario in a run, with error messages and the failing " +
        "steps. Omit runId for the most recent run. An empty list means nothing has " +
        "failed (so far — check run_status for whether the run is still going).")]
    public static string FailingTests(
        MonitorRunRegistry registry,
        [Description("Run id from list_runs; omit for the most recent run.")] string? runId = null)
    {
        var resolved = resolve(registry, runId, out var problem);
        if (resolved == null) return problem!;

        return registry.Read(resolved.Value, run => toJson(new
        {
            runId = run.RunId,
            suite = run.Suite,
            finished = run.Finished,
            failing = run.Scenarios
                .Where(s => s.Outcome is "Failed" or "Aborted")
                .OrderBy(s => s.Uid)
                .Select(s => new
                {
                    uid = s.Uid,
                    outcome = s.Outcome,
                    attempts = s.Attempts,
                    errorMessage = s.ErrorMessage,
                    failedSteps = s.Steps
                        .Where(step => step.Status is not ("ok" or "success" or "running"))
                        .Select(step => new
                        {
                            name = $"{step.Kind} {step.Text}",
                            status = step.Status,
                            errorMessage = step.ErrorMessage
                        })
                        .ToArray()
                })
                .ToArray()
        })) ?? error($"run {resolved} disappeared while reading");
    }

    [McpServerTool(Name = "flaky_ledger")]
    [Description(
        "Scenarios that passed only after retrying — the box's flakiness ledger. These are " +
        "green builds hiding real instability; chronic entries here deserve attention. " +
        "Spans every known run unless runId narrows it.")]
    public static string FlakyLedger(
        MonitorRunRegistry registry,
        [Description("Optional run id to narrow to one run.")] string? runId = null)
    {
        Guid? filter = null;
        if (runId != null)
        {
            if (!Guid.TryParse(runId, out var parsed)) return error($"'{runId}' is not a run id");
            filter = parsed;
        }

        return registry.ReadAll(runs => toJson(runs
            .Where(r => filter == null || r.RunId == filter)
            .SelectMany(r => r.Scenarios
                .Where(s => s.Outcome == "PassOnRetry" || s.RetryReasons.Count > 0)
                .Select(s => new
                {
                    runId = r.RunId,
                    suite = r.Suite,
                    repository = r.Repository,
                    branch = r.Branch,
                    uid = s.Uid,
                    outcome = s.Outcome ?? "running",
                    attempts = s.Attempts ?? s.Attempt,
                    retryReasons = s.RetryReasons
                }))
            .ToArray()));
    }

    [McpServerTool(Name = "await_run_completion")]
    [Description(
        "Block until a run finishes, then return its final summary — kick off a long suite " +
        "and call this instead of polling. Omit runId when exactly one run is in flight. " +
        "Returns early with outcome 'orphaned' if the run's publisher dies, or 'timeout' " +
        "with current progress if timeoutSeconds elapses first.")]
    public static async Task<string> AwaitRunCompletion(
        MonitorRunRegistry registry,
        [Description("Run id from list_runs; omit when exactly one run is in flight.")]
        string? runId = null,
        [Description("Max seconds to wait (default 600, capped at 3600).")]
        int timeoutSeconds = 600,
        CancellationToken cancellationToken = default)
    {
        Guid target;
        if (runId != null)
        {
            if (!Guid.TryParse(runId, out target)) return error($"'{runId}' is not a run id");
        }
        else
        {
            var inFlight = registry.ReadAll(runs =>
                runs.Where(r => !r.Finished && !r.Orphaned).Select(r => new { r.RunId, r.Suite }).ToArray());

            switch (inFlight.Length)
            {
                case 1:
                    target = inFlight[0].RunId;
                    break;
                case 0:
                    // Nothing in flight: report the most recent run immediately rather than
                    // erroring — "the suite you just launched already finished" is the common
                    // race for a fast suite.
                    var latest = registry.ReadAll(runs =>
                        runs.OrderByDescending(r => r.StartedAt ?? DateTimeOffset.MinValue)
                            .Select(r => (Guid?)r.RunId).FirstOrDefault());
                    if (latest == null) return error("no runs known to this monitor");
                    target = latest.Value;
                    break;
                default:
                    return error("several runs are in flight — pass a runId: " +
                                 string.Join(", ", inFlight.Select(r => $"{r.RunId} ({r.Suite})")));
            }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 1, 3600));

        while (true)
        {
            var state = registry.Read(target, r => new { r.Finished, r.Orphaned });
            if (state == null) return error($"run {target} is not known to this monitor");

            if (state.Finished || state.Orphaned)
            {
                return registry.Read(target, run => toJson(new
                {
                    outcome = run.Finished ? "finished" : "orphaned",
                    run = summarize(run)
                }))!;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return registry.Read(target, run => toJson(new
                {
                    outcome = "timeout",
                    waitedSeconds = timeoutSeconds,
                    run = summarize(run)
                }))!;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    [McpServerTool(Name = "export_run")]
    [Description(
        "Export a run's results: 'ctrf' (rich JSON — retries, flakiness, steps) or 'junit' " +
        "(lossy XML for CI ingestion). Omit runId for the most recent run.")]
    public static string ExportRun(
        MonitorRunRegistry registry,
        [Description("Run id from list_runs; omit for the most recent run.")] string? runId = null,
        [Description("'ctrf' (default) or 'junit'.")] string format = "ctrf")
    {
        var resolved = resolve(registry, runId, out var problem);
        if (resolved == null) return problem!;

        var rendered = format.ToLowerInvariant() switch
        {
            "ctrf" => registry.Read(resolved.Value, CtrfExport.Render),
            "junit" => registry.Read(resolved.Value, JUnitExport.Render),
            _ => null
        };

        if (rendered == null && format.ToLowerInvariant() is not ("ctrf" or "junit"))
            return error($"unknown format '{format}' — expected ctrf or junit");

        return rendered ?? error($"run {resolved} is not known to this monitor");
    }

    private static object summarize(RunProjection run)
    {
        var scenarios = run.Scenarios;
        return new
        {
            runId = run.RunId,
            suite = run.Suite,
            repository = run.Repository,
            branch = run.Branch,
            mode = run.Mode,
            finished = run.Finished,
            orphaned = run.Orphaned,
            exitCode = run.ExitCode,
            startedAt = run.StartedAt,
            finishedAt = run.FinishedAt,
            totalScenarios = run.TotalScenarios,
            counts = new
            {
                completed = scenarios.Count(s => s.Outcome != null),
                passed = scenarios.Count(s => s.Outcome is "CleanPass" or "PassOnRetry"),
                failed = scenarios.Count(s => s.Outcome is "Failed" or "Aborted"),
                passedOnRetry = scenarios.Count(s => s.Outcome == "PassOnRetry"),
                running = scenarios.Count(s => s.Outcome == null)
            }
        };
    }

    /// <summary>Parse the optional runId, defaulting to the most recent run.</summary>
    private static Guid? resolve(MonitorRunRegistry registry, string? runId, out string? problem)
    {
        if (runId != null)
        {
            if (Guid.TryParse(runId, out var parsed))
            {
                problem = null;
                return parsed;
            }

            problem = error($"'{runId}' is not a run id");
            return null;
        }

        var latest = registry.ReadAll(runs =>
            runs.OrderByDescending(r => r.StartedAt ?? DateTimeOffset.MinValue)
                .Select(r => (Guid?)r.RunId)
                .FirstOrDefault());

        problem = latest == null ? error("no runs known to this monitor") : null;
        return latest;
    }
}
