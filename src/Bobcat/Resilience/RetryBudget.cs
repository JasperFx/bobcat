namespace Bobcat.Resilience;

/// <summary>
/// Caps how much retrying a run may do, so nothing loops forever and a broadly broken
/// environment cannot burn the whole CI slot re-running everything.
/// </summary>
/// <remarks>
/// Two independent ceilings, because they fail differently: <see cref="MaxAttemptsPerTest"/>
/// stops one pathological test, <see cref="MaxRetriesPerRun"/> stops a pathological
/// <em>environment</em> — the case where every test is failing and each one asks for a retry.
/// </remarks>
public sealed class RetryBudget
{
    private readonly Dictionary<string, int> _attemptsByTest = new();
    private readonly object _lock = new();
    private int _retriesSpent;

    /// <summary>Retries are opt-in: with no budget configured, nothing is ever retried.</summary>
    public static RetryBudget None => new() { MaxAttemptsPerTest = 1 };

    /// <summary>
    /// Total attempts allowed for any one test, including the first. 1 means no retries.
    /// A test's own <c>@retry(N)</c> tag may lower this but never raise it.
    /// </summary>
    public int MaxAttemptsPerTest { get; init; } = 1;

    /// <summary>Retries allowed across the whole run.</summary>
    public int MaxRetriesPerRun { get; init; } = int.MaxValue;

    public int RetriesSpent { get { lock (_lock) return _retriesSpent; } }

    /// <summary>
    /// Attempts allowed for this test: the run ceiling, lowered by the test's own
    /// <c>@retry(N)</c> if it asked for less.
    /// </summary>
    public int AttemptsAllowedFor(IReadOnlyDictionary<string, string> traits)
    {
        var allowed = MaxAttemptsPerTest;

        if (traits.TryGetValue(ResilienceTags.Retry, out var raw) &&
            int.TryParse(raw, out var requested) && requested >= 1)
        {
            // A tag asking for MORE than the run allows is capped, not honoured — the run-level
            // ceiling is the thing an operator sets, and a spec author must not be able to
            // escape it.
            allowed = Math.Min(allowed, requested);
        }

        return Math.Max(1, allowed);
    }

    /// <summary>Retries allowed for one test — one fewer than the attempts it may make.</summary>
    private int RetriesAllowedFor(IReadOnlyDictionary<string, string> traits)
        => AttemptsAllowedFor(traits) - 1;

    /// <summary>True when <paramref name="testId"/> could still be retried after this attempt.</summary>
    public bool CanRetry(string testId, IReadOnlyDictionary<string, string> traits)
    {
        lock (_lock)
        {
            if (_retriesSpent >= MaxRetriesPerRun) return false;

            var used = _attemptsByTest.TryGetValue(testId, out var count) ? count : 0;
            return used < RetriesAllowedFor(traits);
        }
    }

    /// <summary>
    /// Books a retry against the budget. Returns false — and spends nothing — when the budget
    /// is exhausted, with <paramref name="denial"/> explaining which ceiling was hit.
    /// </summary>
    public bool TryConsume(string testId, IReadOnlyDictionary<string, string> traits, out string denial)
    {
        lock (_lock)
        {
            if (_retriesSpent >= MaxRetriesPerRun)
            {
                denial = $"the run's retry budget is exhausted ({MaxRetriesPerRun} retries)";
                return false;
            }

            var used = _attemptsByTest.TryGetValue(testId, out var count) ? count : 0;
            var allowed = RetriesAllowedFor(traits);

            if (used >= allowed)
            {
                denial = allowed == 0
                    ? "retries are not enabled for this test"
                    : $"this test has used all {AttemptsAllowedFor(traits)} allowed attempts";
                return false;
            }

            _attemptsByTest[testId] = used + 1;
            _retriesSpent++;
            denial = "";
            return true;
        }
    }
}
