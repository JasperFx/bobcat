using JasperFx.Testing;
using Bobcat.Engine;

namespace Bobcat.Resilience;

/// <summary>
/// Decides from author-declared <see cref="RecoveryHint"/>s — issue #44 layer 1.
/// </summary>
/// <remarks>
/// <para>
/// Sits between the user's own policies and <see cref="DefaultFailurePolicy"/>: explicit code
/// still wins, and anything no hint describes falls through to the tag-driven default unchanged.
/// A run with no hints behaves exactly as it did before this existed.
/// </para>
/// <para>
/// <strong>This never widens the budget.</strong> A hint saying a failure clears on retry is the
/// author describing the failure, not granting attempts — <see cref="RetryBudget"/> still decides
/// how many there are. The two are separate on purpose: knowledge of what recovers belongs to the
/// person who wrote the test, and how much time the run may spend belongs to whoever runs it.
/// </para>
/// </remarks>
public sealed class HintedFailurePolicy : IFailurePolicy
{
    private readonly RecoveryHintSet _hints;

    public HintedFailurePolicy(RecoveryHintSet hints) => _hints = hints;

    public Disposition? Decide(AttemptContext attempt)
    {
        if (attempt.Succeeded || _hints.IsEmpty) return null;

        // A catastrophic failure means nothing downstream can pass. No hint gets to talk the run
        // out of stopping — the environment is gone, and the author's knowledge was about a test.
        if (attempt.FailureLevel == FailureLevel.Catastrophic ||
            attempt.Exception is SpecCatastrophicException)
        {
            return null;
        }

        var hint = _hints.Best(attempt.TestId, attempt.Failure);
        if (hint is null) return null;

        if (hint.Kind == DispositionKind.FailAndContinue)
        {
            return Disposition.FailAndContinue($"{hint} — declared as never recovering, so it was not retried")
                with { Hint = hint };
        }

        if (!attempt.RetriesAvailable)
        {
            // The hint asked for a retry and the budget refused. Saying so beats reporting a bare
            // failure: the fix is to raise the ceiling, and nothing else in the report says that.
            var exhausted = attempt.AttemptNumber >= attempt.AttemptsAllowed
                ? $"this test has used all {attempt.AttemptsAllowed} allowed attempts"
                : "the run's retry budget is exhausted";

            return Disposition.FailAndContinue($"{hint} — but {exhausted}") with { Hint = hint };
        }

        var next = $"attempt {attempt.AttemptNumber + 1}";

        Disposition? decided = hint.Kind switch
        {
            DispositionKind.RetryInProcess => Disposition.RetryInProcess($"{hint} — {next}"),
            DispositionKind.RetryInFreshProcess => Disposition.RetryInFreshProcess($"{hint} — {next} runs alone"),
            DispositionKind.RetryAfterRecycle => Disposition.RetryAfterRecycle(
                $"{hint} — recycling before {next}", [.. hint.Resources]),
            _ => null
        };

        return decided is null ? null : decided with { Hint = hint };
    }
}
