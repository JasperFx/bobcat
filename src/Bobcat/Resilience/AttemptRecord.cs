namespace Bobcat.Resilience;

/// <summary>
/// How a test finished, once retries are in play.
/// </summary>
/// <remarks>
/// <see cref="PassOnRetry"/> exists because collapsing it into <see cref="CleanPass"/> is how a
/// retry feature becomes a way to launder red into green. A test that needed three attempts and
/// a broker recycle is not the same fact as a test that passed first time, and CI trust depends
/// on the difference staying visible.
/// </remarks>
public enum RunOutcome
{
    /// <summary>Passed on the first attempt.</summary>
    CleanPass,

    /// <summary>Passed, but only after one or more retries. Reported distinctly, never as a clean pass.</summary>
    PassOnRetry,

    /// <summary>Failed on every attempt it was allowed.</summary>
    Failed,

    /// <summary>The run was aborted at this test.</summary>
    Aborted
}

/// <summary>One attempt at one test, and what the policy decided afterwards.</summary>
public sealed record AttemptRecord(
    int AttemptNumber,
    bool Succeeded,
    Disposition Disposition)
{
    /// <summary>
    /// Set when a policy asked for something this runner cannot do yet — a fresh process or a
    /// resource recycle, both of which need the supervisor. Recorded rather than silently
    /// downgraded, so a run report never implies a retry happened when it did not.
    /// </summary>
    public string? Unsupported { get; init; }

    public override string ToString()
        => $"attempt {AttemptNumber}: {(Succeeded ? "passed" : "failed")} — {Disposition}" +
           (Unsupported is null ? "" : $" [not honoured: {Unsupported}]");
}
