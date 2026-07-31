using System.Text.Json;

namespace Bobcat.Monitor.Runs;

/// <summary>
/// Projects a run onto CTRF (Common Test Report Format, ctrf.io) — the monitor's primary eject
/// format, chosen because it is the only CI interchange format with first-class
/// <c>retries</c>/<c>flaky</c>/<c>steps</c> fields, and it is where Microsoft's own MTP report
/// extensions landed. Anything richer than the schema rides the spec's <c>extra</c> object:
/// Bobcat's RunOutcome, uids, retry reasons, and the run's repository identity.
/// </summary>
public static class CtrfExport
{
    private static readonly JsonSerializerOptions serializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

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
                    name = "bobcat-monitor",
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
                    start = run.StartedAt?.ToUnixTimeMilliseconds() ?? 0,
                    stop = run.FinishedAt?.ToUnixTimeMilliseconds() ?? 0
                },
                tests = scenarios.Select(s => new
                {
                    name = $"{s.Feature}: {s.Scenario}",
                    status = s.Outcome switch
                    {
                        "CleanPass" or "PassOnRetry" => "passed",
                        "Failed" => "failed",
                        "Aborted" => "other",
                        _ => "pending"
                    },
                    duration = s.DurationMs ?? 0,
                    suite = s.Feature,
                    retries = Math.Max(0, (s.Attempts ?? s.Attempt) - 1),
                    flaky = s.Outcome == "PassOnRetry",
                    message = s.ErrorMessage,
                    steps = s.Steps.Select(step => new
                    {
                        name = $"{step.Kind} {step.Text}",
                        status = step.Status switch
                        {
                            "ok" or "success" => "passed",
                            "running" => "pending",
                            _ => "failed"
                        }
                    }).ToArray(),
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
