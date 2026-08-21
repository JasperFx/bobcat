using System.Text.Json;

namespace Bobcat.Console.Runs;

/// <summary>
/// Projects a run onto CTRF (Common Test Report Format, ctrf.io) — the monitor's primary eject
/// format, chosen because it is the only CI interchange format with first-class
/// <c>retries</c>/<c>flaky</c>/<c>steps</c> fields, and it is where Microsoft's own MTP report
/// extensions landed. Anything richer than the schema rides the spec's <c>extra</c> object:
/// Bobcat's RunOutcome, uids, retry reasons, and the run's repository identity.
/// </summary>
public static class CtrfExport
{
    // WhenWritingNull keeps the report schema-clean: CTRF's typed fields don't admit null, so
    // "retryAttempts": null on a never-retried test (or "message": null on a passing one) is
    // omitted rather than emitted.
    private static readonly JsonSerializerOptions serializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

    private static object[] renderSteps(IEnumerable<StepProjection> steps)
        => steps.Select(step => (object)new
        {
            name = $"{step.Kind} {step.Text}",
            status = step.Status switch
            {
                "ok" or "success" => "passed",
                "running" => "pending",
                _ => "failed"
            }
        }).ToArray();

    private static string statusOf(string? outcome)
        => outcome switch
        {
            "CleanPass" or "PassOnRetry" => "passed",
            "Failed" => "failed",
            "Aborted" => "other",
            _ => "pending"
        };

    /// <summary>
    /// The spec's retryAttempts[] carries the FULL attempt history — the retried-away
    /// attempts and the final one (the spec's own with-retries example ships
    /// <c>retries: 2</c> with three entries, the last of them passed). Attempt objects allow
    /// no extra members directly, so step detail and the policy's disposition/reason ride
    /// each attempt's <c>extra</c>. Null (omitted) when the test never retried.
    /// </summary>
    private static object[]? renderRetryAttempts(ScenarioProjection s)
    {
        if (s.PriorAttempts.Count == 0) return null;

        var attempts = new List<object>();
        foreach (var attempt in s.PriorAttempts.OrderBy(a => a.Attempt))
        {
            attempts.Add(new
            {
                attempt = attempt.Attempt,
                // Archived by a RetryScheduled (or the next attempt's start): the policy
                // judged this attempt a failure by definition.
                status = "failed",
                message = attempt.ErrorMessage,
                extra = new
                {
                    disposition = attempt.Disposition,
                    reason = attempt.Reason,
                    steps = renderSteps(attempt.Steps)
                }
            });
        }

        // The final attempt's steps are the scenario's live step list. No per-attempt
        // duration: the wire's DurationMs is the whole scenario, and inventing a split
        // would be false precision.
        attempts.Add(new
        {
            attempt = s.Attempt,
            status = statusOf(s.Outcome),
            message = s.ErrorMessage,
            extra = new { steps = renderSteps(s.Steps) }
        });

        return attempts.ToArray();
    }

    public static string Render(RunProjection run)
    {
        var scenarios = run.Scenarios.OrderBy(s => s.Feature).ThenBy(s => s.Scenario).ToArray();

        var report = new
        {
            reportFormat = "CTRF",
            specVersion = "1.0.0",
            results = new
            {
                tool = new
                {
                    name = "bobcat",
                    version = typeof(CtrfExport).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"
                },
                summary = new
                {
                    tests = scenarios.Length,
                    passed = scenarios.Count(s => s.Outcome is "CleanPass" or "PassOnRetry"),
                    failed = scenarios.Count(s => s.Outcome == "Failed"),
                    // A scenario without a terminal outcome (exported mid-run, or the publisher
                    // died) is pending — never silently counted as passed or failed.
                    pending = scenarios.Count(s => s.Outcome is null),
                    skipped = 0,
                    other = scenarios.Count(s => s.Outcome == "Aborted"),
                    flaky = scenarios.Count(s => s.Outcome == "PassOnRetry"),
                    start = run.StartedAt?.ToUnixTimeMilliseconds() ?? 0,
                    stop = run.FinishedAt?.ToUnixTimeMilliseconds() ?? 0
                },
                tests = scenarios.Select(s => new
                {
                    name = $"{s.Feature}: {s.Scenario}",
                    status = statusOf(s.Outcome),
                    duration = s.DurationMs ?? 0,
                    // The spec types suite as an ARRAY ("suite hierarchy from top-level to
                    // immediate parent") — a bare string fails schema validation. Found by
                    // validating a live export against schema/ctrf.schema.json.
                    suite = new[] { s.Feature },
                    retries = Math.Max(0, (s.Attempts ?? s.Attempt) - 1),
                    flaky = s.Outcome == "PassOnRetry",
                    message = s.ErrorMessage,
                    steps = renderSteps(s.Steps),
                    retryAttempts = renderRetryAttempts(s),
                    extra = new
                    {
                        uid = s.Uid,
                        outcome = s.Outcome,
                        retryReasons = s.RetryReasons
                    }
                }).ToArray(),
                extra = new
                {
                    runId = run.RunId,
                    repository = run.Repository,
                    branch = run.Branch,
                    mode = run.Mode,
                    exitCode = run.ExitCode,
                    totalScenarios = run.TotalScenarios,
                    finished = run.Finished
                }
            }
        };

        return JsonSerializer.Serialize(report, serializerOptions);
    }
}
