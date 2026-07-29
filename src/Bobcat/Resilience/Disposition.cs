namespace Bobcat.Resilience;

/// <summary>
/// What the supervisor should do about one attempt at one test.
/// </summary>
/// <remarks>
/// This is <see cref="Bobcat.Engine.IContinuationRule"/> grown up. That interface answers a
/// binary <c>ShouldStop</c> at the wrong altitude — inside the process that may need replacing.
/// A disposition is decided by a policy and acted on by whoever owns the process boundary.
/// </remarks>
public enum DispositionKind
{
    /// <summary>The attempt succeeded.</summary>
    Pass,

    /// <summary>An ordinary failure: record it and keep going. Descends from <c>FailureLevel.Assertion</c>.</summary>
    FailAndContinue,

    /// <summary>Try again in the same process, after resources are reset.</summary>
    RetryInProcess,

    /// <summary>
    /// Try again in a brand-new process, with this test running alone. For the Marten/Wolverine
    /// tests that only pass when nothing else shares their process.
    /// </summary>
    RetryInFreshProcess,

    /// <summary>
    /// Throw the named resources away, stand fresh ones up, then try again. For brokers whose
    /// in-flight state cannot be reliably drained the way a database is truncated.
    /// </summary>
    RetryAfterRecycle,

    /// <summary>
    /// Stop the run now — nothing downstream can pass. Descends from
    /// <c>FailureLevel.Catastrophic</c> / <see cref="Bobcat.Engine.SpecCatastrophicException"/>.
    /// </summary>
    AbortRun
}

/// <summary>
/// A policy's decision about one attempt, plus the reason — which is reported to the user, so
/// it must read as an explanation rather than a code.
/// </summary>
public sealed record Disposition
{
    private Disposition(DispositionKind kind, string reason, IReadOnlyList<string> resources)
    {
        Kind = kind;
        Reason = reason;
        Resources = resources;
    }

    public DispositionKind Kind { get; }

    /// <summary>Why the policy decided this. Surfaced in the run report.</summary>
    public string Reason { get; }

    /// <summary>Resources to recycle. Only meaningful for <see cref="DispositionKind.RetryAfterRecycle"/>.</summary>
    public IReadOnlyList<string> Resources { get; }

    public bool IsRetry => Kind is DispositionKind.RetryInProcess
        or DispositionKind.RetryInFreshProcess
        or DispositionKind.RetryAfterRecycle;

    /// <summary>True when acting on this needs a supervisor above the process boundary.</summary>
    public bool RequiresSupervisor => Kind is DispositionKind.RetryInFreshProcess
        or DispositionKind.RetryAfterRecycle;

    public static readonly Disposition Pass = new(DispositionKind.Pass, "passed", []);

    public static Disposition FailAndContinue(string reason)
        => new(DispositionKind.FailAndContinue, reason, []);

    public static Disposition RetryInProcess(string reason)
        => new(DispositionKind.RetryInProcess, reason, []);

    public static Disposition RetryInFreshProcess(string reason)
        => new(DispositionKind.RetryInFreshProcess, reason, []);

    public static Disposition RetryAfterRecycle(string reason, params string[] resources)
    {
        if (resources.Length == 0)
        {
            throw new ArgumentException(
                "RetryAfterRecycle needs at least one resource name — otherwise it is just RetryInProcess.",
                nameof(resources));
        }

        return new Disposition(DispositionKind.RetryAfterRecycle, reason, resources);
    }

    public static Disposition AbortRun(string reason)
        => new(DispositionKind.AbortRun, reason, []);

    public override string ToString()
        => Resources.Count > 0
            ? $"{Kind}({string.Join(", ", Resources)}): {Reason}"
            : $"{Kind}: {Reason}";
}
