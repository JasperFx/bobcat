using Bobcat.Engine;

namespace Bobcat.Resilience;

/// <summary>
/// Everything a policy is allowed to see about one attempt at one test.
/// </summary>
/// <remarks>
/// Deliberately framework-neutral: no Gherkin, no <c>ScenarioDefinition</c>, no
/// <c>ExecutionResults</c>. A Bobcat scenario, an xUnit fact and a tUnit test all project onto
/// this shape, which is what keeps the resilience engine reusable across front-ends.
/// </remarks>
public sealed class AttemptContext
{
    /// <summary>Stable identity for the test across attempts and processes.</summary>
    public required string TestId { get; init; }

    /// <summary>Human-readable name, for the report.</summary>
    public required string Title { get; init; }

    /// <summary>1-based. The first attempt is 1, so a retry is always <c>&gt; 1</c>.</summary>
    public required int AttemptNumber { get; init; }

    public required bool Succeeded { get; init; }

    /// <summary>The worst failure level any step reached on this attempt.</summary>
    public FailureLevel FailureLevel { get; init; } = FailureLevel.None;

    /// <summary>The first exception thrown, when there was one.</summary>
    public Exception? Exception { get; init; }

    private readonly FailureSignature? _failure;

    /// <summary>
    /// The failure's class, for matching <see cref="RecoveryHint"/>s.
    /// </summary>
    /// <remarks>
    /// Defaults to the inheritance chain of <see cref="Exception"/>, which is right in process.
    /// A front-end that learned the type name some other way — a supervisor reading it off the
    /// MTP wire, where the exception object never existed — sets this instead, because deriving
    /// it from the stand-in exception would report the wrapper's type rather than the failure's.
    /// </remarks>
    public FailureSignature Failure
    {
        get => _failure ?? FailureSignature.FromException(Exception);
        init => _failure = value;
    }

    /// <summary>
    /// Key/value metadata: Gherkin tags projected by <see cref="ResilienceTags"/>, or traits read
    /// off an xUnit <c>[Trait]</c> / tUnit <c>[Property]</c> through the MTP wire. The spike for
    /// issue #43 found traits are the only metadata channel that survives every front-end
    /// intact, so policy keys off these rather than off exception types.
    /// </summary>
    public IReadOnlyDictionary<string, string> Traits { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the run's retry budget would still allow another attempt.</summary>
    public bool RetriesAvailable { get; init; }

    /// <summary>
    /// Total attempts this test is allowed. Carried so a policy can explain *why* it stopped
    /// retrying — "used all 3 allowed attempts" is a useful report line; "failed" is not.
    /// </summary>
    public int AttemptsAllowed { get; init; } = 1;

    public bool HasTrait(string name) => Traits.ContainsKey(name);

    public string? Trait(string name) => Traits.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// Maps an attempt onto a <see cref="Disposition"/>. Register implementations on the runner to
/// override or extend <see cref="DefaultFailurePolicy"/>.
/// </summary>
public interface IFailurePolicy
{
    /// <summary>
    /// Return null to abstain — the next policy decides, and if all abstain the default applies.
    /// Abstaining rather than returning FailAndContinue is what lets narrow policies compose.
    /// </summary>
    Disposition? Decide(AttemptContext attempt);
}

/// <summary>
/// Runs policies in order and takes the first non-null answer.
/// </summary>
public sealed class FailurePolicyChain : IFailurePolicy
{
    private readonly IReadOnlyList<IFailurePolicy> _policies;

    public FailurePolicyChain(params IFailurePolicy[] policies) => _policies = policies;

    public Disposition? Decide(AttemptContext attempt)
    {
        foreach (var policy in _policies)
        {
            var disposition = policy.Decide(attempt);
            if (disposition is not null) return disposition;
        }

        return null;
    }
}
