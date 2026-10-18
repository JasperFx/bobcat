namespace Bobcat.Resilience;

/// <summary>
/// What a test runner should do about an attempt. The vocabulary a <see cref="RecoveryHintAttribute"/>
/// declares against, and the kind of every <see cref="Disposition"/>.
/// </summary>
/// <remarks>
/// <para>
/// Bobcat's own since bobcat#744. It lived in JasperFx (<c>JasperFx.Testing</c>) from #63 so a suite could
/// annotate itself without referencing Bobcat; nobody did, and the price was a JasperFx floor in every
/// consumer's restore. The members and their order are unchanged, and the wire carries a disposition by
/// name, so nothing downstream changes.
/// </para>
/// <para>
/// One enum rather than a copy mapped at a seam: two enums meaning the same thing is how a vocabulary
/// starts drifting.
/// </para>
/// </remarks>
public enum DispositionKind
{
    /// <summary>The attempt succeeded.</summary>
    Pass,

    /// <summary>An ordinary failure: record it and keep going.</summary>
    FailAndContinue,

    /// <summary>Try again in the same process, after resources are reset.</summary>
    RetryInProcess,

    /// <summary>
    /// Try again in a brand-new process, with this test running alone. For the tests that only
    /// pass when nothing else shares their process.
    /// </summary>
    RetryInFreshProcess,

    /// <summary>
    /// Throw the named resources away, stand fresh ones up, then try again. For brokers whose
    /// in-flight state cannot be reliably drained the way a database is truncated.
    /// </summary>
    RetryAfterRecycle,

    /// <summary>Stop the run now — nothing downstream can pass.</summary>
    AbortRun
}
