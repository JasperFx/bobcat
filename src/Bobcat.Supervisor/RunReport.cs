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

        return report.ToString().TrimEnd();
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
            ["recyclings"] = new JsonArray(results.Recyclings.Select(r => (JsonNode)r!).ToArray()),
            ["workerFaults"] = new JsonArray(results.WorkerFaults.Select(f => (JsonNode)f!).ToArray()),
            ["unsupportedDispositions"] =
                new JsonArray(results.UnsupportedDispositions.Select(u => (JsonNode)u!).ToArray()),
            ["quarantine"] = new JsonArray(results.Quarantine.Select(t => (JsonNode)t.Uid!).ToArray()),
            ["tests"] = new JsonArray(results.Tests.Select(describe).ToArray<JsonNode>())
        };

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

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
