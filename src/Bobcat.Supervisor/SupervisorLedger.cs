using Bobcat.Ledger;

namespace Bobcat.Supervisor;

/// <summary>
/// The supervisor's feed into the committed ledger (<see cref="TestLedger"/>) — the richer of
/// the two, because a supervised run keeps every attempt's outcome: the failure type a
/// pass-on-retry recovered from, the first attempt's own duration, and whether the attempts
/// were the stall escalation's doing.
/// </summary>
public static class SupervisorLedger
{
    /// <summary>
    /// Extract one run's observations. <paramref name="runId"/> and <paramref name="at"/> are
    /// the caller's facts — the monitor run id when publishing, a fresh guid otherwise — and
    /// deliberately not read from a clock here, because the fold must stay deterministic.
    /// </summary>
    public static IReadOnlyList<LedgerRun> From(SupervisorResults results, string runId, DateTimeOffset at)
    {
        var observations = new List<LedgerRun>();

        foreach (var test in results.Tests)
        {
            var durations = test.Attempts.Select(a => a.Outcome.Duration).ToList();
            var measured = durations.Any(d => d is not null);

            // The failure class is the first failing attempt that reported a type — the same
            // name FailureSignature matches on, because out of process a name is all there is.
            var failure = test.Attempts
                .Where(a => !a.Succeeded)
                .Select(a => a.Outcome.ErrorType)
                .FirstOrDefault(t => t is not null);

            observations.Add(new LedgerRun(
                runId, at, test.Uid, test.DisplayName, test.Outcome.ToString(), test.AttemptCount)
            {
                TotalMs = measured
                    ? (long)durations.Sum(d => d?.TotalMilliseconds ?? 0)
                    : null,
                FirstMs = durations[0] is { } first ? (long)first.TotalMilliseconds : null,
                Failure = failure,
                ClearedBy = test.Outcome == Bobcat.Resilience.RunOutcome.PassOnRetry && test.Attempts.Count > 1
                    ? test.Attempts[^2].Disposition.Kind.ToString()
                    : null,
                StallInduced = test.Attempts.Any(a => a.StallInduced)
            });
        }

        return observations;
    }
}
