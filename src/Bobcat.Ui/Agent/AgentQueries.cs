using System.Text.Json;
using Bobcat.Ui.EventModel;
using Bobcat.Ui.Runs;
using JasperFx.Events.EventModeling;

namespace Bobcat.Ui.Agent;

/// <summary>
/// The agent-facing reads over the board and the Event Model (bobcat#524): what Stoat served as
/// MCP tools, served here over HTTP at <c>/api/agent/*</c> (<see cref="AgentEndpoints"/>) and read
/// by the <c>bobcat</c> CLI's query commands, so an agent asks with a shell command rather than an
/// MCP client. Each returns camelCase JSON; a problem is <c>{ "error": "…" }</c>.
///
/// All reads go through the registry's locked Read/ReadAll so a projection mutating under
/// live ingestion can never hand a query a torn scenario collection.
/// </summary>
public static class AgentQueries
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Read in a terminal as often as by a program: an apostrophe stays an apostrophe.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string toJson(object value) => JsonSerializer.Serialize(value, jsonOptions);

    private static string error(string message) => toJson(new { error = message });

    /// <summary>Every run this console knows — running, finished, and orphaned (rehydrated from an archive after its publisher died). Newest first.</summary>
    public static string ListRuns(MonitorRunRegistry registry)
    {
        return registry.ReadAll(runs => toJson(runs
            .OrderByDescending(r => r.StartedAt ?? DateTimeOffset.MinValue)
            .Select(summarize)
            .ToArray()));
    }

    /// <summary>Progress detail for one run (the most recent when <paramref name="runId"/> is null): per-scenario outcomes, the running scenario's live steps, and for a supervised run its lanes, recycles, worker faults, stalls and latest progress heartbeat.</summary>
    public static string RunStatus(
        MonitorRunRegistry registry,
        string? runId = null)
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

    /// <summary>Every failed or aborted scenario in a run, with its error and failing steps. Empty means nothing has failed so far.</summary>
    public static string FailingTests(
        MonitorRunRegistry registry,
        string? runId = null)
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
                        .Where(step => StepVerdict.IsFailed(step.Status))
                        .Select(step => new
                        {
                            name = nameOf(step),
                            status = step.Status,
                            errorMessage = step.ErrorMessage,
                            // The named comparisons that disagreed (stoat#58) — "expected 6, got 8"
                            // per cell is the diagnosis a flattened message could never carry.
                            failedCells = failedCellsOf(step),
                            exception = exceptionOf(step)
                        })
                        .ToArray()
                })
                .ToArray()
        })) ?? error($"run {resolved} disappeared while reading");
    }

    /// <summary>Scenarios that passed only after retrying, across every known run unless <paramref name="runId"/> narrows it.</summary>
    public static string FlakyLedger(
        MonitorRunRegistry registry,
        string? runId = null)
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

    /// <summary>Block until a run finishes or orphans, then return its summary; <c>timeout</c> with current progress if the wait runs out first.</summary>
    public static async Task<string> AwaitRunCompletion(
        MonitorRunRegistry registry,
        string? runId = null,
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

    /// <summary>A run's results as <c>ctrf</c> (rich JSON) or <c>junit</c> (lossy XML for CI).</summary>
    public static string ExportRun(
        MonitorRunRegistry registry,
        string? runId = null,
        string format = "ctrf")
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

    /// <summary>The merged Event Model, optionally narrowed to one slice or one domain.</summary>
    public static string EventModel(
        EventModelStore store,
        string? slice = null,
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

    /// <summary>What is untested, per slice: <c>no-spec</c> (nothing bound) versus <c>no-evidence</c> (bound, never run), with each bound spec's last outcome.</summary>
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

    /// <summary>Full detail for one scenario — every step, cells, logs, exception frames, prior attempts, touched types and the slices it is bound to. The run's first failing scenario when <paramref name="uid"/> is null.</summary>
    public static string FailingSpec(
        MonitorRunRegistry registry,
        EventModelStore store,
        string? runId = null,
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
        name = nameOf(step),
        status = step.Status,
        durationMs = step.DurationMs,
        errorMessage = step.ErrorMessage,
        // Every cell, green ones included (stoat#58): a wrong product must not hide a right sum,
        // for an agent reading this any more than for a person reading the run view.
        cells = step.Cells.Count == 0
            ? null
            : step.Cells.Select(c => new
            {
                name = c.Name,
                status = c.Status,
                expected = c.Expected,
                actual = c.Actual,
                note = c.Note,
                row = c.RowIndex < 0 ? (int?)null : c.RowIndex
            }).ToArray(),
        logs = step.Logs.Count == 0 ? null : step.Logs,
        diagnostics = step.Diagnostics.Count == 0 ? null : step.Diagnostics,
        exception = exceptionOf(step)
    };

    /// <summary>
    /// A step's name as a sentence. An empty kind is a step that spells no keyword (stoat#59), so
    /// it contributes nothing — not a leading space, and never a borrowed "Then".
    /// </summary>
    private static string nameOf(StepProjection step)
        => string.IsNullOrEmpty(step.Kind) ? step.Text : $"{step.Kind} {step.Text}";

    private static object[]? failedCellsOf(StepProjection step)
    {
        var failed = step.Cells.Where(c => c.Status is not ("success" or "ok")).ToArray();
        return failed.Length == 0
            ? null
            : failed.Select(c => rowMarker(c) is { } marker
                    ? (object)new
                    {
                        name = c.Name,
                        status = c.Status,
                        row = c.RowIndex,
                        values = absentRowValues(step, c.RowIndex, marker)
                    }
                    : new { name = c.Name, status = c.Status, expected = c.Expected, actual = c.Actual })
                .ToArray();
    }

    /// <summary>
    /// Bobcat reports a set verification's absent row as ONE marker cell — <c>missing-row</c> or
    /// <c>extra-row</c> — beside an <c>ok</c> cell per column carrying the row's values (stoat#63).
    /// The siblings are <c>ok</c> so the row counts once, which is exactly why a failed-cells list
    /// drops them; without folding them back in, an agent learns THAT a row was missing and never
    /// which one.
    /// </summary>
    private static string? rowMarker(Contracts.StepCell cell) => cell.RowIndex < 0
        ? null
        : cell.Name switch
        {
            "missing-row" => "missing",
            "extra-row" => "extra",
            _ => null
        };

    /// <summary>A missing row's expected values, or an extra row's actual ones, by column.</summary>
    private static Dictionary<string, string?> absentRowValues(StepProjection step, int row, string marker)
        => step.Cells
            .Where(c => c.RowIndex == row && rowMarker(c) is null)
            .GroupBy(c => c.Name)
            .ToDictionary(g => g.Key, g => marker == "missing" ? g.First().Expected : g.First().Actual);

    /// <summary>
    /// The filtered frames, not the raw stack (stoat#60): Bobcat already removed its own plumbing and
    /// the runner's, and says how many it removed, so an agent reads the frames that matter.
    /// </summary>
    private static object? exceptionOf(StepProjection step) => step.Exception is not { } exception
        ? null
        : new
        {
            type = exception.Type,
            frames = exception.Frames.Count > 0 ? exception.Frames : null,
            hiddenFrames = exception.HiddenFrames,
            stackTrace = exception.Frames.Count > 0 ? null : exception.StackTrace
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
            // stoat#64: the canonical owner/name, so an agent can match a run to its plan's repository.
            repositorySlug = run.RepositorySlug,
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
