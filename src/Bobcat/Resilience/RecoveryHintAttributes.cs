namespace Bobcat.Resilience;

/// <summary>
/// Declares that a named class of failure, on the tests in scope, recovers a particular way.
/// </summary>
/// <remarks>
/// <para>
/// These are the author's half of issue #44: knowledge someone already has, written down where
/// the test lives instead of being rediscovered by every reader of a red build. A
/// <see cref="ResilienceTags.Retry"/> tag says "this test is unreliable"; a hint says
/// <em>which</em> failure is unreliable and what fixes it, so an assertion failure on the same
/// test is still reported as the bug it is.
/// </para>
/// <para>
/// Hints never widen the retry budget. <see cref="RetryBudget.MaxAttemptsPerTest"/> is the
/// operator's ceiling and a hint cannot raise it — an unconfigured run still retries nothing,
/// however many hints are declared.
/// </para>
/// <para>
/// Applicable to a class (every test it owns — for Gherkin, the fixture's whole feature), a
/// method, or an assembly. The narrowest scope wins; see <see cref="RecoveryHintSet.Best"/>.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Assembly,
    AllowMultiple = true)]
public abstract class RecoveryHintAttribute : Attribute
{
    protected RecoveryHintAttribute(Type failureType) => FailureType = failureType;

    /// <summary>The exception type this hint describes. Base types match derived failures.</summary>
    public Type FailureType { get; }

    /// <summary>
    /// Why the author believes this. Reaches the run report verbatim, so it should read as an
    /// explanation to whoever is looking at the retry six months from now.
    /// </summary>
    public string? Because { get; set; }

    /// <summary>What to do about it.</summary>
    public abstract DispositionKind Kind { get; }

    /// <summary>Resources to recycle. Only meaningful for <see cref="ClearsOnRecycleAttribute"/>.</summary>
    public virtual IReadOnlyList<string> Resources => [];
}

/// <summary>This failure clears by running the test again in the same process.</summary>
/// <example><c>[ClearsOnRetry(typeof(TimeoutException), Because = "the broker is slow to warm up")]</c></example>
public sealed class ClearsOnRetryAttribute(Type failureType) : RecoveryHintAttribute(failureType)
{
    public override DispositionKind Kind => DispositionKind.RetryInProcess;
}

/// <summary>
/// This failure clears only in a brand-new process, with the test running alone — the shape of
/// leak that a scope reset cannot undo, like a static cached the first time anything touched it.
/// </summary>
public sealed class ClearsInFreshProcessAttribute(Type failureType) : RecoveryHintAttribute(failureType)
{
    public override DispositionKind Kind => DispositionKind.RetryInFreshProcess;
}

/// <summary>
/// This failure clears only after the named resources are thrown away and stood up fresh.
/// </summary>
/// <example><c>[ClearsOnRecycle("rabbit", typeof(BrokerUnavailableException))]</c></example>
/// <remarks>
/// <paramref name="resources"/> is comma-separated, matching the <c>@recycle(rabbit,kafka)</c>
/// tag it shares a vocabulary with.
/// </remarks>
public sealed class ClearsOnRecycleAttribute(string resources, Type failureType)
    : RecoveryHintAttribute(failureType)
{
    public override DispositionKind Kind => DispositionKind.RetryAfterRecycle;

    public override IReadOnlyList<string> Resources { get; } = ResilienceTags.ParseResources(resources);
}

/// <summary>
/// This failure never clears, so do not spend attempts on it.
/// </summary>
/// <remarks>
/// The counterweight to the rest of the file. Without it, the only way to stop a broad
/// <c>@retry(3)</c> from re-running a deterministic bug three times is to take the tag off the
/// test — which also stops the retries that were pulling their weight.
/// </remarks>
public sealed class NeverRecoversAttribute(Type failureType) : RecoveryHintAttribute(failureType)
{
    public override DispositionKind Kind => DispositionKind.FailAndContinue;
}
