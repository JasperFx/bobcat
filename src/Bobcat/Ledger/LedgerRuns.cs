using Bobcat.Runtime;

namespace Bobcat.Ledger;

/// <summary>
/// The in-process runner's feed into the committed ledger: one <see cref="LedgerRun"/> per
/// scenario of a <see cref="SuiteResults"/>. The supervisor's richer feed is
/// <c>Bobcat.Supervisor</c>'s <c>SupervisorLedger</c>.
/// </summary>
public static class LedgerRuns
{
    /// <summary>
    /// Extract one run's observations. <paramref name="runId"/> and <paramref name="at"/> come
    /// from the caller because they are the run's facts — the monitor run id when publishing,
    /// a fresh guid otherwise — and because the fold itself must never read a clock.
    /// </summary>
    /// <remarks>
    /// Two honest gaps of the in-process shape, both absent-not-guessed: durations are the #141
    /// bracket wall clock, so a scenario captured without one contributes no figure; and only
    /// the final attempt's <see cref="Engine.ExecutionResults"/> survives in-process, so a
    /// pass-on-retry records how it was cleared but not the failure type it recovered from —
    /// the supervisor feed, which keeps every attempt's outcome, records both.
    /// </remarks>
    public static IReadOnlyList<LedgerRun> From(SuiteResults results, string runId, DateTimeOffset at)
    {
        var observations = new List<LedgerRun>();

        foreach (var feature in results.Features)
        {
            foreach (var scenario in feature.Scenarios)
            {
                var uid = $"{feature.Title}/{scenario.Title}";
                var measured = scenario.Results.WallClockMs > 0 || scenario.Results.Timeline.Count > 0;
                var totalMs = measured ? scenario.Results.WallClockMs : (long?)null;

                observations.Add(new LedgerRun(
                    runId, at, uid, uid, scenario.Outcome.ToString(), scenario.AttemptCount)
                {
                    TotalMs = totalMs,
                    // Only the final attempt is measured in-process; with one attempt the two
                    // figures are the same fact.
                    FirstMs = scenario.AttemptCount == 1 ? totalMs : null,
                    Failure = scenario.Outcome is Resilience.RunOutcome.Failed or Resilience.RunOutcome.Aborted
                        ? scenario.Results.AllExceptions().FirstOrDefault()?.GetType().FullName
                        : null,
                    ClearedBy = scenario.Outcome == Resilience.RunOutcome.PassOnRetry && scenario.Attempts.Count > 1
                        ? scenario.Attempts[^2].Disposition.Kind.ToString()
                        : null
                });
            }
        }

        return observations;
    }
}
