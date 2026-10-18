using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bobcat.Resilience;

namespace Bobcat.Supervisor;

/// <summary>
/// Renders a supervised run two ways: for a person reading a CI log, and for a machine.
/// </summary>
/// <remarks>
/// Both views report <see cref="RunOutcome.CleanPass"/> and <see cref="RunOutcome.PassOnRetry"/>
/// as different facts, and neither ever folds one into the other. Retries hide flakiness and
/// silent green destroys CI trust; if resilience shipped without honest reporting, the feature
/// would just be a way to launder red into green.
/// </remarks>
public static partial class RunReport
{
    /// <summary>The human view.</summary>
    public static string ToText(SupervisorResults results)
    {
        var report = new StringBuilder();

        if (results.AbortReason is not null)
        {
            report.AppendLine($"RUN ABORTED: {results.AbortReason}").AppendLine();
        }

        if (results.CollapsedFailure is { } collapsedFailure)
        {
            report.AppendLine($"RUN FAILED: {collapsedFailure}").AppendLine();
        }

        report.AppendLine(results.Summarize());

        // A plan that silently fell back to counting tests reads exactly like a balanced one
        // (bobcat#663), so the match is stated whenever durations were supplied.
        if (results.DurationCoverage is { } coverage)
        {
            report.AppendLine(coverage.ToString());
        }

        // Stalls lead, beside the retries (bobcat#704): a stalled test that eventually passed
        // still exceeded its budget, and in a green run a section below the fold goes unread —
        // Polecat's flat_table_works_with_async_daemon stalled in 10 of 78 jobs and showed only
        // as a log line in the green ones.
        if (results.StalledTests.Count > 0)
        {
            report.AppendLine().AppendLine("Stalled (in flight past the stall threshold):");
            foreach (var stalled in results.StalledTests)
            {
                report.AppendLine($"  • {stalled.DisplayName} — {stallOf(stalled, results)}");
            }
        }

        // What the failed attempt failed WITH is what decides whether a test belongs on retries
        // or is a real bug (bobcat#662), so a pass-on-retry carries it.
        section(report, "Passed on retry (not clean passes)", results.PassedOnRetry,
            test => $"{test.DisplayName} — {test.AttemptCount} attempts, {placements(test)}" +
                    (RecoveredFrom(test) is { } failure ? $"; first failed with {failure}" : ""));

        section(report, "Failed", results.Failed,
            test => $"{test.DisplayName} — {reason(test)}");

        // Which author-declared hints actually fired. A hint that suppressed a retry has to be
        // visible, or a tagged test that failed once looks like the tag stopped working.
        var hinted = results.Tests
            .SelectMany(test => test.Attempts.Select(attempt => (test, attempt.Disposition.Hint)))
            .Where(pair => pair.Hint is not null)
            .DistinctBy(pair => (pair.test.Uid, pair.Hint!.FailureTypeName, pair.Hint.Kind))
            .ToList();

        if (hinted.Count > 0)
        {
            report.AppendLine().AppendLine("Recovery hints applied:");
            foreach (var (test, hint) in hinted)
            {
                report.AppendLine($"  ↯ {test.DisplayName} — {hint}");
            }
        }

        section(report, "Indeterminate (result never established)", results.Indeterminate,
            test => $"{test.DisplayName} — {test.Final.Outcome.ErrorMessage ?? "no result reported"}");

        // A collapsed theory is judged by its worst row, but the run still saw fewer tests than a
        // direct run would, and the fix is in the suite — so it is named even when it passed.
        section(report, "Collapsed theories (several rows reported under one test id; make the theory data serializable)",
            results.Collapsed, test => collapsed(test, everyRow: results.CollapsedFailure is not null));

        section(report, "Quarantine candidates (needed more than one attempt)", results.Quarantine,
            test => $"{test.DisplayName} — {test.AttemptCount} attempts, final: {test.Outcome}");

        if (results.UnsupportedDispositions.Count > 0)
        {
            report.AppendLine().AppendLine("Retry requests that were NOT honoured:");
            foreach (var unsupported in results.UnsupportedDispositions)
            {
                report.AppendLine($"  ! {unsupported}");
            }
        }

        // The kills are their own ledger, apart from worker faults and apart from the flaky
        // ledger (issue #173): a wedge is not a crash and not a flake, and a run that only
        // stayed green because a wedged worker was shot must say so.
        if (results.StallKills.Count > 0)
        {
            report.AppendLine().AppendLine("Workers killed to clear a stall (StallAction.KillAndRetry):");
            foreach (var kill in results.StallKills)
            {
                report.AppendLine(
                    $"  ↯ {kill.DisplayName} — killed after {(int)kill.InFlight.TotalSeconds}s in flight" +
                    (kill.Lane is { } lane ? $" on lane {lane}" : " in a solo process") +
                    (kill.ProcessId is { } pid ? $" (pid {pid})" : ""));
            }
        }

        timing(report, results);
        memory(report, results);

        return report.ToString().TrimEnd();
    }

    /// <summary>How many of the slowest tests to name, and to concentrate over.</summary>
    private const int SlowestReported = 5;

    /// <summary>
    /// Where the run spent its time. Reported for every run, not only slow ones — a test that
    /// silently grew from 2s to 40s is invisible in the run that first got slow.
    /// </summary>
    private static void timing(StringBuilder report, SupervisorResults results)
    {
        // Nothing timed the run: hand-built results, or a caller that never went through
        // Supervisor.Run. Saying nothing beats printing zeroes as though they were measurements.
        if (results.Duration <= TimeSpan.Zero) return;

        var timing = RunTiming.For(results);

        report.AppendLine().AppendLine($"Timing (wall clock {RunTiming.Humanize(timing.WallClock)}):");

        if (!timing.IsMeasured)
        {
            // tUnit erases durations on the MTP wire the same way it erases exception types. Say
            // so — an empty timing section reads as "the run was instant".
            report.AppendLine("  • no test reported a duration, so there is nothing to attribute " +
                              "the run's wall clock to");
            return;
        }

        report.AppendLine($"  • {RunTiming.Humanize(timing.Measured)} measured across " +
                          $"{timing.Tests.Count} test(s) — {efficiencyOf(timing)}");

        // The percentage is what makes someone act; the raw seconds read as "integration tests
        // are slow".
        var slowest = timing.Slowest(SlowestReported);
        report.AppendLine(
            $"  • the slowest test is {RunTiming.Percent(timing.Share(slowest[0].Total)!.Value)} of wall clock" +
            (slowest.Count > 1
                ? $"; the slowest {slowest.Count} are {RunTiming.Percent(timing.Concentration(SlowestReported)!.Value)}"
                : ""));

        if (timing.LaunchOverhead >= TimeSpan.FromMilliseconds(1))
        {
            report.AppendLine($"  • {RunTiming.Humanize(timing.LaunchOverhead)} launching " +
                              $"{results.WorkersLaunched} worker process(es)");
        }

        if (timing.RetryCost > TimeSpan.Zero)
        {
            report.AppendLine($"  • {RunTiming.Humanize(timing.RetryCost)} of the run was retries");
        }

        if (timing.IsolationCost > TimeSpan.Zero)
        {
            report.AppendLine($"  • {RunTiming.Humanize(timing.IsolationCost)} of the run was tests " +
                              "running alone (the price of isolation)");
        }

        if (timing.Unmeasured > 0)
        {
            report.AppendLine($"  ! {timing.Unmeasured} test(s) reported no duration — these figures " +
                              "are a floor, not a total");
        }

        if (timing.RunnerSpeed is { } speed)
        {
            report.AppendLine($"  • runner speed {ratio(speed.Index)} the known durations of the same " +
                              $"{speed.Matched} test(s) ({RunTiming.Humanize(speed.ThisRun)} this run against " +
                              $"{RunTiming.Humanize(speed.Known)} known; above 1 is slower)");
        }

        lanePlan(report, timing);

        report.AppendLine("  Slowest:");
        foreach (var test in slowest)
        {
            var share = RunTiming.Percent(timing.Share(test.Total)!.Value);
            var retried = test.Attempts > 1
                ? $", {test.Attempts} attempts costing {RunTiming.Humanize(test.RetryCost)} extra"
                : "";

            report.AppendLine($"    • {RunTiming.Humanize(test.Total)} ({share}) {test.DisplayName}{retried}");
        }
    }

    private static string ratio(double value) => value.ToString("0.00", CultureInfo.InvariantCulture) + "x";

    /// <summary>
    /// The first pass's lanes, predicted against realised (bobcat#705). Report, don't act: this is
    /// the evidence for whether duration balancing helped, with the runner's speed taken out.
    /// </summary>
    private static void lanePlan(StringBuilder report, RunTiming timing)
    {
        if (timing.LanePlan.Count < 2) return;

        var line = new StringBuilder(timing.BalancedByDuration
            ? $"  • lane plan (balanced by duration): predicted makespan {RunTiming.Humanize(timing.PredictedMakespan!.Value)}"
            : "  • lane plan (balanced by test count, so no predicted time)");

        if (timing.SpeedAdjustedMakespan is { } adjusted)
        {
            line.Append($" ({RunTiming.Humanize(adjusted)} at this runner's speed)");
        }

        line.Append($", realised {RunTiming.Humanize(timing.RealisedMakespan!.Value)} of test time");
        if (timing.RealisedLaneWallClock is { } wall) line.Append($" in {RunTiming.Humanize(wall)} of lane wall clock");

        if (timing.PredictedImbalance is { } predicted && timing.RealisedImbalance is { } realised)
        {
            line.Append($"; slowest lane over the mean {ratio(predicted)} planned, {ratio(realised)} realised");
        }

        report.AppendLine(line.ToString());

        foreach (var lane in timing.LanePlan)
        {
            report.AppendLine(
                $"    • lane {lane.Index}: {lane.Tests} test(s)" +
                (lane.Predicted is { } p ? $", predicted {RunTiming.Humanize(p)}" : "") +
                $", test time {RunTiming.Humanize(lane.TestTime)}" +
                (lane.Elapsed is { } e ? $", wall clock {RunTiming.Humanize(e)}" : ""));
        }
    }

    /// <summary>
    /// Parallel efficiency, split into where the overlap came from (bobcat#664). A test
    /// framework's in-process parallelism inside one worker is not the supervisor's lanes, and
    /// reading one as the other misleads the decision the figure exists for: whether more workers
    /// will help.
    /// </summary>
    private static string efficiencyOf(RunTiming timing)
    {
        static string x(double value) => value.ToString("0.00", CultureInfo.InvariantCulture) + "x";

        var overall = $"{x(timing.ParallelEfficiency!.Value)} parallel efficiency";
        if (timing.LaneConcurrency is not { } lanes || timing.InWorkerConcurrency is not { } inWorker) return overall;

        if (timing.Lanes <= 1)
        {
            return $"{overall}, all of it inside one worker ({x(inWorker)} in-process test parallelism; " +
                   "one lane, so none of it is the supervisor's)";
        }

        return $"{overall}: {x(lanes)} from {timing.Lanes} lanes running side by side, " +
               $"{x(inWorker)} in-process test parallelism within a worker";
    }

    /// <summary>How many of the biggest memory retainers to name.</summary>
    private const int RetainersReported = 10;

    /// <summary>
    /// Where the run spent its memory (issue #149). Says nothing at all when nothing was
    /// sampled — an empty section full of zeroes would read as "the run used no memory".
    /// </summary>
    private static void memory(StringBuilder report, SupervisorResults results)
    {
        var resources = RunResources.For(results);

        // A target somebody named is worth a line even unmeasured: "not measured" is the clue
        // that docker was not reachable, where silence would read as "nothing to report".
        if (!resources.IsMeasured && resources.Targets.Count == 0) return;

        report.AppendLine().AppendLine(resources.PeakBytes is { } peak
            ? $"Memory (peak worker RSS {RunResources.Humanize(peak)}):"
            : "Memory (no worker measured):");

        foreach (var worker in resources.Workers)
        {
            report.AppendLine(
                $"  • {describeWorker(worker.Worker)}: {RunResources.Humanize(worker.FirstBytes)} → " +
                $"{RunResources.Humanize(worker.PeakBytes)} peak ({RunResources.Delta(worker.GrowthBytes)})");
        }

        foreach (var target in resources.Targets)
        {
            report.AppendLine(target is { FirstBytes: { } first, PeakBytes: { } targetPeak, GrowthBytes: { } growth }
                ? $"  • {target.Name}: {RunResources.Humanize(first)} → " +
                  $"{RunResources.Humanize(targetPeak)} peak ({RunResources.Delta(growth)})"
                : $"  • {target.Name}: not measured");
        }

        if (resources.Attributed.Count > 0)
        {
            report.AppendLine("  Top retainers:");
            foreach (var test in resources.TopRetainers(RetainersReported))
            {
                report.AppendLine($"    • {RunResources.Delta(test.RetainedBytes!.Value)} {test.DisplayName}");
            }
        }

        if (resources.Unattributed > 0)
        {
            report.AppendLine($"  ! {resources.Unattributed} attempt(s) shared their process with " +
                              "other tests — those deltas are not attributed to anyone");
        }
    }

    private static string describeWorker(WorkerLaunchContext worker)
    {
        var name = worker.Purpose == WorkerPurpose.Lane
            ? $"lane {worker.Lane} worker"
            : $"{worker.Purpose.ToString().ToLowerInvariant()} worker";

        return worker.ProcessId is { } pid ? $"{name} (pid {pid})" : name;
    }

    private static void section(
        StringBuilder report, string title, IReadOnlyList<TestReport> tests, Func<TestReport, string> describe)
    {
        if (tests.Count == 0) return;

        report.AppendLine().AppendLine($"{title}:");
        foreach (var test in tests) report.AppendLine($"  • {describe(test)}");
    }

    private static string collapsed(TestReport test, bool everyRow)
    {
        var rows = test.Attempts
            .Select(a => a.Outcome.CollapsedDisplayNames)
            .LastOrDefault(names => names is { Count: > 1 })!;

        // A run failing on its collapses names every row, because each one is something to fix.
        var shown = everyRow ? rows.Count : 5;
        var named = string.Join("; ", rows.Take(shown));
        var more = rows.Count > shown ? $"; …and {rows.Count - shown} more" : "";

        return $"{test.DisplayName} — {rows.Count} rows, judged {test.Outcome} by the worst: {named}{more}";
    }

    /// <summary>How much of a failure message a report repeats — enough to recognise it.</summary>
    internal const int FailureMessageLimit = 200;

    /// <summary>
    /// The failure a pass-on-retry recovered from (bobcat#662): the first attempt that failed for
    /// the test's own reasons, never one the supervisor's stall kill manufactured. Null when no
    /// attempt failed that way.
    /// </summary>
    internal static RecoveredFailure? RecoveredFrom(TestReport test)
    {
        var failed = test.Attempts.FirstOrDefault(a => !a.Succeeded && !a.StallInduced);
        if (failed is null) return null;

        return new RecoveredFailure(
            failed.AttemptNumber, failed.Outcome.State, failed.Outcome.ErrorType,
            Bounded(withoutType(failed.Outcome.ErrorMessage, failed.Outcome.ErrorType)));
    }

    /// <summary>The first line of a message, cut to <see cref="FailureMessageLimit"/> with an ellipsis.</summary>
    internal static string? Bounded(string? message)
    {
        var line = message?.Split('\n')[0].Trim();
        if (string.IsNullOrEmpty(line)) return null;

        return line.Length <= FailureMessageLimit ? line : line[..FailureMessageLimit] + "…";
    }

    /// <summary>
    /// xUnit writes the type at the head of the message ("Ns.Type : message"), which is where the
    /// type was recovered from, so the report says it once.
    /// </summary>
    private static string? withoutType(string? message, string? type)
        => type is not null && message?.StartsWith(type + " : ", StringComparison.Ordinal) == true
            ? message[(type.Length + 3)..]
            : message;

    /// <summary>
    /// One stall: where it ran, how long it had been in flight when it crossed the threshold, and
    /// how the test ended — a stall that went on to finish is a different story from a wedge.
    /// </summary>
    private static string stallOf(StalledTest stalled, SupervisorResults results)
    {
        var where = stalled.Worker.Purpose switch
        {
            WorkerPurpose.Isolated => "an isolated process",
            WorkerPurpose.Recycled => "a recycled process",
            WorkerPurpose.Discovery => "the discovery process",
            _ => $"lane {stalled.Worker.Lane}"
        };

        var line = $"{where}, {(int)stalled.InFlight.TotalSeconds}s in flight when it stalled";

        var test = results.Tests.FirstOrDefault(t => t.Uid == stalled.Uid);
        if (test is null) return $"{line}; never finished (no result recorded)";

        var final = test.Final;
        var attempts = test.AttemptCount > 1 ? $" on attempt {final.AttemptNumber}" : "";

        if (final.StallInduced || final.Outcome.State == WorkerTestState.Indeterminate)
        {
            return $"{line}; never finished{attempts}";
        }

        var ran = final.Outcome.Duration is { } duration ? $" after {RunTiming.Humanize(duration)}" : "";
        return $"{line}; finished {final.Outcome.State}{ran}{attempts}";
    }

    private static string placements(TestReport test)
        => string.Join(" → ", test.Attempts.Select(a => a.Placement.ToString()));

    private static string reason(TestReport test)
        => test.Final.Outcome.ErrorMessage?.Split('\n')[0].Trim() is { Length: > 0 } message
            ? message
            : test.Final.Outcome.State.ToString();

    /// <summary>
    /// The machine view — Bobcat project goal #2's AI outbox. Structured rather than scraped:
    /// an agent asking "which tests are flaky and under what conditions" should read fields,
    /// not parse a console log.
    /// </summary>
    public static string ToJson(SupervisorResults results)
    {
        var document = new JsonObject
        {
            ["exitCode"] = results.ExitCode,
            ["abortReason"] = results.AbortReason,
            ["failOnCollapsed"] = results.FailOnCollapsed,
            ["collapsedFailure"] = results.CollapsedFailure,
            ["partial"] = results.IsPartial,
            ["summary"] = new JsonObject
            {
                ["cleanPass"] = results.CleanPasses.Count,
                ["passOnRetry"] = results.PassedOnRetry.Count,
                ["failed"] = results.Failed.Count,
                ["indeterminate"] = results.Indeterminate.Count,
                ["skipped"] = results.Skipped.Count,
                ["retriesPerformed"] = results.RetriesPerformed,
                ["workersLaunched"] = results.WorkersLaunched
            },
            ["knownDurations"] = results.DurationCoverage is not { } coverage
                ? null
                : new JsonObject
                {
                    ["matched"] = coverage.Matched,
                    ["tests"] = coverage.Tests,
                    ["balancedBy"] = coverage.BalancedByCount ? "count" : "duration"
                },
            ["timing"] = describe(RunTiming.For(results)),
            ["memory"] = describe(RunResources.For(results)),
            ["stalled"] = new JsonArray(results.StalledTests.Select(s => (JsonNode)new JsonObject
            {
                ["uid"] = s.Uid,
                ["displayName"] = s.DisplayName,
                ["inFlightMs"] = s.InFlight.TotalMilliseconds,
                ["lane"] = s.Worker.Lane
            }).ToArray()),
            ["stallKills"] = new JsonArray(results.StallKills.Select(k => (JsonNode)new JsonObject
            {
                ["uid"] = k.Uid,
                ["displayName"] = k.DisplayName,
                ["inFlightMs"] = k.InFlight.TotalMilliseconds,
                ["lane"] = k.Lane,
                ["processId"] = k.ProcessId
            }).ToArray()),
            ["recyclings"] = new JsonArray(results.Recyclings.Select(r => (JsonNode)r!).ToArray()),
            ["workerFaults"] = new JsonArray(results.WorkerFaults.Select(f => (JsonNode)f!).ToArray()),
            ["unsupportedDispositions"] =
                new JsonArray(results.UnsupportedDispositions.Select(u => (JsonNode)u!).ToArray()),
            ["quarantine"] = new JsonArray(results.Quarantine.Select(t => (JsonNode)t.Uid!).ToArray()),
            ["collapsed"] = new JsonArray(results.Collapsed.Select(t => (JsonNode)t.Uid!).ToArray()),
            ["tests"] = new JsonArray(results.Tests.Select(describe).ToArray<JsonNode>())
        };

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// The timing block. Every figure that could not be measured is <c>null</c> rather than 0 —
    /// an agent reading this must be able to tell "no time was spent" from "nobody measured".
    /// </summary>
    private static JsonObject describe(RunTiming timing) => new()
    {
        ["wallClockMs"] = timing.WallClock > TimeSpan.Zero ? timing.WallClock.TotalMilliseconds : null,
        ["measuredMs"] = timing.IsMeasured ? timing.Measured.TotalMilliseconds : null,
        ["parallelEfficiency"] = timing.IsMeasured ? timing.ParallelEfficiency : null,
        // bobcat#664: where the overlap came from — the supervisor's lanes, or the test
        // framework's own parallelism inside a worker. Null when nothing measured worker time.
        ["lanes"] = timing.Lanes,
        ["workerBusyMs"] = timing.WorkerBusy > TimeSpan.Zero ? timing.WorkerBusy.TotalMilliseconds : null,
        ["laneConcurrency"] = timing.IsMeasured ? timing.LaneConcurrency : null,
        ["inWorkerConcurrency"] = timing.IsMeasured ? timing.InWorkerConcurrency : null,
        ["workerLaunchMs"] = timing.LaunchOverhead > TimeSpan.Zero ? timing.LaunchOverhead.TotalMilliseconds : null,
        ["retryCostMs"] = timing.IsMeasured ? timing.RetryCost.TotalMilliseconds : null,
        ["isolationCostMs"] = timing.IsMeasured ? timing.IsolationCost.TotalMilliseconds : null,
        ["testsWithoutDuration"] = timing.Unmeasured,
        // bobcat#705: this runner against the known durations of the same tests, and the lane
        // plan's prediction against what the lanes took. Null where nothing measured it.
        ["runnerSpeed"] = timing.RunnerSpeed is not { } speed
            ? null
            : new JsonObject
            {
                ["index"] = speed.Index,
                ["matched"] = speed.Matched,
                ["thisRunMs"] = speed.ThisRun.TotalMilliseconds,
                ["knownMs"] = speed.Known.TotalMilliseconds
            },
        ["lanePlan"] = timing.LanePlan.Count < 2
            ? null
            : new JsonObject
            {
                ["balancedBy"] = timing.BalancedByDuration ? "duration" : "count",
                ["predictedMakespanMs"] = timing.PredictedMakespan?.TotalMilliseconds,
                ["speedAdjustedMakespanMs"] = timing.SpeedAdjustedMakespan?.TotalMilliseconds,
                ["realisedMakespanMs"] = timing.RealisedMakespan?.TotalMilliseconds,
                ["realisedLaneWallClockMs"] = timing.RealisedLaneWallClock?.TotalMilliseconds,
                ["predictedImbalance"] = timing.PredictedImbalance,
                ["realisedImbalance"] = timing.RealisedImbalance,
                ["lanes"] = new JsonArray(timing.LanePlan.Select(lane => (JsonNode)new JsonObject
                {
                    ["index"] = lane.Index,
                    ["tests"] = lane.Tests,
                    ["predictedMs"] = lane.Predicted?.TotalMilliseconds,
                    ["testTimeMs"] = lane.TestTime.TotalMilliseconds,
                    ["elapsedMs"] = lane.Elapsed?.TotalMilliseconds
                }).ToArray())
            },
        ["concentration"] = !timing.IsMeasured
            ? null
            : new JsonObject
            {
                ["slowest"] = timing.Concentration(1),
                [$"slowest{SlowestReported}"] = timing.Concentration(SlowestReported)
            },
        ["slowest"] = new JsonArray(timing.Slowest(SlowestReported).Select(test => (JsonNode)new JsonObject
        {
            ["uid"] = test.Uid,
            ["displayName"] = test.DisplayName,
            ["totalMs"] = test.Total.TotalMilliseconds,
            ["firstAttemptMs"] = test.FirstAttempt.TotalMilliseconds,
            ["retryCostMs"] = test.RetryCost.TotalMilliseconds,
            ["attempts"] = test.Attempts,
            ["shareOfRun"] = timing.Share(test.Total)
        }).ToArray())
    };

    /// <summary>
    /// The memory block. Same honesty rule as timing: unmeasured is <c>null</c>, never 0 — an
    /// agent reading this must be able to tell "no memory grew" from "nobody sampled".
    /// </summary>
    private static JsonObject describe(RunResources resources) => new()
    {
        ["peakBytes"] = resources.PeakBytes,
        ["unattributed"] = resources.Unattributed,
        ["workers"] = new JsonArray(resources.Workers.Select(worker => (JsonNode)new JsonObject
        {
            ["lane"] = worker.Worker.Purpose == WorkerPurpose.Lane ? worker.Worker.Lane : null,
            ["purpose"] = worker.Worker.Purpose.ToString(),
            ["pid"] = worker.Worker.ProcessId,
            ["firstBytes"] = worker.FirstBytes,
            ["peakBytes"] = worker.PeakBytes,
            ["lastBytes"] = worker.LastBytes,
            ["growthBytes"] = worker.GrowthBytes
        }).ToArray()),
        ["targets"] = new JsonArray(resources.Targets.Select(target => (JsonNode)new JsonObject
        {
            ["name"] = target.Name,
            ["firstBytes"] = target.FirstBytes,
            ["peakBytes"] = target.PeakBytes,
            ["lastBytes"] = target.LastBytes,
            ["growthBytes"] = target.GrowthBytes
        }).ToArray()),
        ["topRetainers"] = new JsonArray(resources.TopRetainers(RetainersReported)
            .Select(test => (JsonNode)new JsonObject
            {
                ["uid"] = test.Uid,
                ["displayName"] = test.DisplayName,
                ["retainedBytes"] = test.RetainedBytes
            }).ToArray())
    };

    private static JsonObject describe(TestReport test) => new()
    {
        ["uid"] = test.Uid,
        ["displayName"] = test.DisplayName,
        ["outcome"] = test.Outcome.ToString(),
        ["attempts"] = test.AttemptCount,
        ["quarantineCandidate"] = test.WasRetried,
        ["recoveredFrom"] = test.Outcome != RunOutcome.PassOnRetry || RecoveredFrom(test) is not { } failure
            ? null
            : new JsonObject
            {
                ["attempt"] = failure.Attempt,
                ["state"] = failure.State.ToString(),
                ["errorType"] = failure.ErrorType,
                ["message"] = failure.Message
            },
        ["attemptDetail"] = new JsonArray(test.Attempts.Select(attempt => (JsonNode)new JsonObject
        {
            ["attempt"] = attempt.AttemptNumber,
            ["state"] = attempt.Outcome.State.ToString(),
            ["placement"] = attempt.Placement.ToString(),
            ["disposition"] = attempt.Disposition.Kind.ToString(),
            ["reason"] = attempt.Disposition.Reason,
            ["recycled"] = attempt.Disposition.Resources.Count == 0
                ? null
                : new JsonArray(attempt.Disposition.Resources.Select(r => (JsonNode)r!).ToArray()),
            ["notHonoured"] = attempt.Unsupported,
            ["hint"] = attempt.Disposition.Hint is not { } hint
                ? null
                : new JsonObject
                {
                    ["failureType"] = hint.FailureTypeName,
                    ["recovery"] = hint.Kind.ToString(),
                    ["because"] = hint.Because,
                    ["declaredOn"] = hint.Source
                },
            ["errorType"] = attempt.Outcome.ErrorType,
            ["errorMessage"] = attempt.Outcome.ErrorMessage,
            ["collapsedRows"] = attempt.Outcome.CollapsedDisplayNames is { Count: > 1 } rows
                ? new JsonArray(rows.Select(r => (JsonNode)r!).ToArray())
                : null,
            ["durationMs"] = attempt.Outcome.Duration?.TotalMilliseconds
        }).ToArray())
    };
}

/// <summary>
/// What a pass-on-retry's failed attempt failed with (bobcat#662) — the type name a recovery hint
/// would match on and a bounded first line of the message.
/// </summary>
public sealed record RecoveredFailure(int Attempt, WorkerTestState State, string? ErrorType, string? Message)
{
    public override string ToString()
    {
        var what = ErrorType ?? State.ToString().ToLowerInvariant();
        return Message is null ? $"{what} (attempt {Attempt})" : $"{what}: {Message} (attempt {Attempt})";
    }
}
