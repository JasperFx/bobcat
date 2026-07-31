using JasperFx.Testing;

namespace Bobcat.Resilience;

// DispositionKind used to be declared here. It now lives in JasperFx (JasperFx.Testing), along
// with the recovery-hint attributes that declare against it, so a test project can annotate itself
// without referencing Bobcat — see issue #63. Bobcat keeps everything that *acts* on a disposition:
// the record below, IFailurePolicy, the chain, and the budget.
//
// One enum rather than a Bobcat copy mapped at a seam. Two enums meaning the same thing is how the
// vocabulary drifts, and the drift would be invisible until a hint silently stopped matching.
//
// Kept in mind for whoever reads Disposition next: this is IContinuationRule grown up. That
// interface answers a binary ShouldStop at the wrong altitude — inside the process that may need
// replacing. A disposition is decided by a policy and acted on by whoever owns the process
// boundary. FailAndContinue descends from FailureLevel.Assertion, AbortRun from
// FailureLevel.Catastrophic / SpecCatastrophicException.

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

    /// <summary>
    /// The author-declared <see cref="RecoveryHint"/> behind this decision, when one was. Null
    /// for the ordinary tag-driven case.
    /// </summary>
    /// <remarks>
    /// Carried structurally rather than left to be read out of <see cref="Reason"/>, because the
    /// case that most needs reporting is a hint that <em>suppressed</em> a retry: without this a
    /// tagged test would just fail once, and the author would be left wondering why their tag
    /// stopped working.
    /// </remarks>
    public RecoveryHint? Hint { get; init; }

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
