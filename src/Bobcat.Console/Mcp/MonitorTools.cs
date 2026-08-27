using System.ComponentModel;
using System.Text.Json;
using Bobcat.Console.EventModel;
using Bobcat.Console.Runs;
using JasperFx.Events.EventModeling;
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
        "scenario's live steps, and retry activity. For a supervised run also the worker " +
        "lanes (what each was handed, what it is running now, whether it finished or " +
        "crashed, its worker's pid), resources recycled before a retry, every worker process " +
        "that died with its lane, exit code and last standard error, tests reported as " +
        "stalled (in flight past their threshold), and the supervisor's latest progress " +
        "heartbeat with the longest-running test and peak worker RSS — all empty or null for " +
        "an in-process run. Omit runId for the most recent run.")]
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
                .ToArray(),
            // The supervisor's topology (issue #84), folded server-side from the same events
            // the dashboard renders. Always present so an agent never has to guess whether the
            // field is missing or the run simply had no lanes: empty arrays for an in-process run.
            lanes = run.Lanes.Select(l => new
            {
                lane = l.Lane,
                status = l.Status,
                passes = l.Passes,
                uids = l.Uids,
                // What the lane's worker is on right now — the uids it was handed joined to
                // live scenario state. Empty for a foreign-framework worker that streams no
                // scenarios, and for a lane that has finished.
                running = run.RunningIn(l).Select(s => s.Uid).ToArray(),
                startedAt = l.StartedAt,
                finishedAt = l.FinishedAt,
                outcomes = l.Outcomes,
                // The worker's OS pid (issue #146) — what an external diagnostic must target.
                processId = l.ProcessId
            }).ToArray(),
            recycles = run.Recycles.Select(r => new { resource = r.Resource, at = r.At }).ToArray(),
            workerFaults = run.WorkerFaults.Select(f => new
            {
                lane = f.Lane,
                fault = f.Fault,
                exitCode = f.ExitCode,
                standardError = f.StandardError,
                at = f.At
            }).ToArray(),
            // Tests in flight past their stall threshold (issue #145) — the name of the hung
            // test, which is exactly what an agent staring at a wedged run needs first.
            stalls = run.Stalls.Select(s => new
            {
                uid = s.Uid,
                displayName = s.DisplayName,
                inFlightMs = s.InFlightMs,
                lane = s.Lane,
                at = s.At
            }).ToArray(),
            // The supervisor's latest progress heartbeat (issue #148); null until one arrives.
            // For a foreign-framework worker this is the run's only live progress.
            progress = run.Progress is { } p
                ? new
                {
                    elapsedMs = p.ElapsedMs,
                    completed = p.Completed,
                    total = p.Total,
                    inFlight = p.InFlight,
                    longestRunningUid = p.LongestRunningUid,
                    longestRunningDisplayName = p.LongestRunningDisplayName,
                    longestRunningMs = p.LongestRunningMs,
                    peakWorkerRssBytes = p.PeakWorkerRssBytes,
                    at = p.At
                }
                : null
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

    // ----- Spec Driven Development reads (issue #167): the Event Model and its join to run
    // evidence, paired with the critterstack-sdd-* skills. All three are reads over data that
    // already exists — the pushed descriptor (#108) and the spec identity + touched types
    // published on scenario_finished (#106/#107).

    [McpServerTool(Name = "event_model")]
    [Description(
        "The Event Model: every slice with its command, handler, aggregates, emitted events, " +
        "read models, published messages and bound specifications — the map of the system. " +
        "Empty until a producer pushes one (PUT /api/event-model; e.g. Wolverine's " +
        "`event-model --url`). A whole model can be a lot of tokens, so narrow with slice or " +
        "domain when you only need part of it.")]
    public static string EventModel(
        EventModelStore store,
        [Description("Narrow to the one slice with this name (case-insensitive).")]
        string? slice = null,
        [Description("Narrow to the slices in this domain (case-insensitive).")]
        string? domain = null)
    {
        var json = store.Read();
        if (json == null)
            return error("no event model has been pushed to this console — see PUT /api/event-model");
        if (slice == null && domain == null) return json;

        var descriptor = readModel(json, out var problem);
        if (descriptor == null) return problem!;

        var slices = descriptor.Slices
            .Where(s => slice == null || string.Equals(s.Name, slice, StringComparison.OrdinalIgnoreCase))
            .Where(s => domain == null || string.Equals(s.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (slices.Count == 0)
        {
            // Same manners as the generator's BOBCAT012: an unmatched name lists what exists.
            return error("nothing matched — the model's slices are: " + string.Join(", ",
                descriptor.Slices.Select(s => s.Domain == null ? s.Name : $"{s.Name} (domain {s.Domain})")));
        }

        return JsonSerializer.Serialize(descriptor with { Slices = slices }, EventModelStore.Wire);
    }

    [McpServerTool(Name = "slice_coverage")]
    [Description(
        "What is untested, per Event Model slice. The two gaps are distinguished because they " +
        "imply different actions: 'no-spec' (nothing bound — the slice was never specified, so " +
        "scaffold scenarios) and 'no-evidence' (specs bound but no known run ever executed " +
        "them — run the suite, or the spec exists and never executes). A covered slice lists " +
        "each spec's last outcome and finish time, so a slice whose only spec is red is " +
        "visible too. Evidence joins by spec identity {Feature}/{Scenario} across every run " +
        "this console knows.")]
    public static string SliceCoverage(EventModelStore store, MonitorRunRegistry registry)
    {
        var json = store.Read();
        if (json == null)
            return error("no event model has been pushed to this console — see PUT /api/event-model");

        var descriptor = readModel(json, out var problem);
        if (descriptor == null) return problem!;

        // The latest completed verdict per spec identity, across every run this console knows.
        // A scenario still running is not evidence yet.
        var evidence = registry.ReadAll(runs => runs
            .SelectMany(r => r.Scenarios
                .Where(s => s.Outcome != null)
                .Select(s => new SpecEvidence(r.RunId, r.Suite, s.Uid, s.Outcome!, s.FinishedAt)))
            .GroupBy(e => e.Uid)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(e => e.FinishedAt ?? DateTimeOffset.MinValue).First()));

        var slices = descriptor.Slices.Select(s =>
        {
            var specs = s.Specifications.Select(spec =>
            {
                var seen = evidence.GetValueOrDefault(spec.Identity);
                return new
                {
                    identity = spec.Identity,
                    lastOutcome = seen?.Outcome,
                    lastFinishedAt = seen?.FinishedAt,
                    lastRunId = seen?.RunId,
                    lastSuite = seen?.Suite
                };
            }).ToArray();

            var gap = s.Specifications.Count == 0 ? "no-spec"
                : specs.All(x => x.lastOutcome == null) ? "no-evidence"
                : null;

            return new
            {
                slice = s.Name,
                domain = s.Domain,
                pattern = s.Pattern?.ToString(),
                gap,
                specs
            };
        }).ToArray();

        return toJson(new
        {
            model = descriptor.Name,
            summary = new
            {
                slices = slices.Length,
                noSpec = slices.Count(s => s.gap == "no-spec"),
                noEvidence = slices.Count(s => s.gap == "no-evidence"),
                covered = slices.Count(s => s.gap == null)
            },
            slices
        });
    }

    [McpServerTool(Name = "failing_spec")]
    [Description(
        "Full detail for one scenario — the input for writing the code a red spec describes. " +
        "Everything failing_tests summarizes away: every step of the final attempt with its " +
        "status, duration and error, prior attempts with why the policy retried them, the CLR " +
        "types the scenario observably touched (which aggregate/projection/handler to open), " +
        "and the Event Model slices the spec is bound to. Omit uid for the run's first " +
        "failing scenario; omit runId for the most recent run.")]
    public static string FailingSpec(
        MonitorRunRegistry registry,
        EventModelStore store,
        [Description("Run id from list_runs; omit for the most recent run.")] string? runId = null,
        [Description(
            "Spec identity {Feature}/{Scenario}; omit for the run's first failing scenario. " +
            "A uid naming a passing scenario still returns its detail.")]
        string? uid = null)
    {
        var resolved = resolve(registry, runId, out var problem);
        if (resolved == null) return problem!;

        return registry.Read(resolved.Value, run =>
        {
            ScenarioProjection? scenario;
            if (uid != null)
            {
                scenario = run.Scenarios.FirstOrDefault(s => s.Uid == uid);
                if (scenario == null) return error($"run {run.RunId} has no scenario '{uid}'");
            }
            else
            {
                scenario = run.Scenarios
                    .Where(s => s.Outcome is "Failed" or "Aborted")
                    .OrderBy(s => s.Uid)
                    .FirstOrDefault();
                if (scenario == null)
                {
                    return toJson(new
                    {
                        runId = run.RunId,
                        suite = run.Suite,
                        finished = run.Finished,
                        message = run.Finished
                            ? "nothing failed in this run"
                            : "nothing has failed so far — the run is still going"
                    });
                }
            }

            return toJson(new
            {
                runId = run.RunId,
                suite = run.Suite,
                uid = scenario.Uid,
                feature = scenario.Feature,
                scenario = scenario.Scenario,
                status = scenario.Outcome ?? "running",
                attempts = scenario.Attempts ?? scenario.Attempt,
                durationMs = scenario.DurationMs,
                errorMessage = scenario.ErrorMessage,
                finishedAt = scenario.FinishedAt,
                steps = scenario.Steps.Select(renderStep).ToArray(),
                priorAttempts = scenario.PriorAttempts.Select(a => new
                {
                    attempt = a.Attempt,
                    disposition = a.Disposition,
                    reason = a.Reason,
                    errorMessage = a.ErrorMessage,
                    steps = a.Steps.Select(renderStep).ToArray()
                }).ToArray(),
                // Run evidence (issue #107): observed, never asserted — which aggregate,
                // command, events and read model this scenario actually reached.
                touchedTypes = scenario.TouchedTypes.Select(t => new
                {
                    name = t.Name,
                    fullName = t.FullName,
                    assemblyName = t.AssemblyName
                }).ToArray(),
                slices = slicesBoundTo(store, scenario.Uid)
            });
        }) ?? error($"run {resolved} disappeared while reading");
    }

    private static object renderStep(StepProjection step) => new
    {
        name = $"{step.Kind} {step.Text}",
        status = step.Status,
        durationMs = step.DurationMs,
        errorMessage = step.ErrorMessage
    };

    /// <summary>One spec identity's most recent completed verdict, for the coverage join.</summary>
    private sealed record SpecEvidence(
        Guid RunId,
        string Suite,
        string Uid,
        string Outcome,
        DateTimeOffset? FinishedAt);

    /// <summary>
    /// Parse the stored descriptor. TryStore normalized it, so this only fails for a file
    /// hand-edited or corrupted on disk — reported rather than thrown, like every other
    /// tool-level problem.
    /// </summary>
    private static EventModelDescriptor? readModel(string json, out string? problem)
    {
        try
        {
            var descriptor = JsonSerializer.Deserialize<EventModelDescriptor>(json, EventModelStore.Wire);
            problem = descriptor == null ? error("the stored event model is empty") : null;
            return descriptor;
        }
        catch (JsonException e)
        {
            problem = error($"the stored event model is unreadable: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The Event Model slices a spec identity is bound to — best-effort: no model, or an
    /// unreadable one, just yields none, because the join is context on a failing spec rather
    /// than the answer.
    /// </summary>
    private static object[] slicesBoundTo(EventModelStore store, string uid)
    {
        var json = store.Read();
        if (json == null) return [];

        var descriptor = readModel(json, out _);
        return descriptor?.Slices
            .Where(s => s.Specifications.Any(spec => spec.Identity == uid))
            .Select(object (s) => new { slice = s.Name, domain = s.Domain, pattern = s.Pattern?.ToString() })
            .ToArray() ?? [];
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
