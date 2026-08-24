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
public static class RunReport
{
    /// <summary>The human view.</summary>
    public static string ToText(SupervisorResults results)
    {
        var report = new StringBuilder();

        if (results.AbortReason is not null)
        {
            report.AppendLine($"RUN ABORTED: {results.AbortReason}").AppendLine();
        }

        report.AppendLine(results.Summarize());

        section(report, "Passed on retry (not clean passes)", results.PassedOnRetry,
            test => $"{test.DisplayName} — {test.AttemptCount} attempts, {placements(test)}");

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

        // A stalled test that eventually passed still exceeded its budget, and a green run is
        // exactly where that would otherwise go unnoticed — same reasoning as Quarantine.
        if (results.StalledTests.Count > 0)
        {
            report.AppendLine().AppendLine("Stalled (in flight past the stall threshold):");
            foreach (var stalled in results.StalledTests)
            {
                report.AppendLine(
                    $"  • {stalled.DisplayName} — {(int)stalled.InFlight.TotalSeconds}s in flight " +
                    $"on lane {stalled.Worker.Lane}");
            }
        }

        timing(report, results);

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

        var efficiency = timing.ParallelEfficiency!.Value.ToString("0.00", CultureInfo.InvariantCulture);
        report.AppendLine($"  • {RunTiming.Humanize(timing.Measured)} measured across " +
                          $"{timing.Tests.Count} test(s) — {efficiency}x parallel efficiency");

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

    private static void section(
        StringBuilder report, string title, IReadOnlyList<TestReport> tests, Func<TestReport, string> describe)
    {
        if (tests.Count == 0) return;

        report.AppendLine().AppendLine($"{title}:");
        foreach (var test in tests) report.AppendLine($"  • {describe(test)}");
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
            ["summary"] = new JsonObject
            {
                ["cleanPass"] = results.CleanPasses.Count,
                ["passOnRetry"] = results.PassedOnRetry.Count,
                ["failed"] = results.Failed.Count,
                ["indeterminate"] = results.Indeterminate.Count,
                ["retriesPerformed"] = results.RetriesPerformed,
                ["workersLaunched"] = results.WorkersLaunched
            },
            ["timing"] = describe(RunTiming.For(results)),
            ["stalled"] = new JsonArray(results.StalledTests.Select(s => (JsonNode)new JsonObject
            {
                ["uid"] = s.Uid,
                ["displayName"] = s.DisplayName,
                ["inFlightMs"] = s.InFlight.TotalMilliseconds,
                ["lane"] = s.Worker.Lane
            }).ToArray()),
            ["recyclings"] = new JsonArray(results.Recyclings.Select(r => (JsonNode)r!).ToArray()),
            ["workerFaults"] = new JsonArray(results.WorkerFaults.Select(f => (JsonNode)f!).ToArray()),
            ["unsupportedDispositions"] =
                new JsonArray(results.UnsupportedDispositions.Select(u => (JsonNode)u!).ToArray()),
            ["quarantine"] = new JsonArray(results.Quarantine.Select(t => (JsonNode)t.Uid!).ToArray()),
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
        ["workerLaunchMs"] = timing.LaunchOverhead > TimeSpan.Zero ? timing.LaunchOverhead.TotalMilliseconds : null,
        ["retryCostMs"] = timing.IsMeasured ? timing.RetryCost.TotalMilliseconds : null,
        ["isolationCostMs"] = timing.IsMeasured ? timing.IsolationCost.TotalMilliseconds : null,
        ["testsWithoutDuration"] = timing.Unmeasured,
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

    private static JsonObject describe(TestReport test) => new()
    {
        ["uid"] = test.Uid,
        ["displayName"] = test.DisplayName,
        ["outcome"] = test.Outcome.ToString(),
        ["attempts"] = test.AttemptCount,
        ["quarantineCandidate"] = test.WasRetried,
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
            ["durationMs"] = attempt.Outcome.Duration?.TotalMilliseconds
        }).ToArray())
    };
}
