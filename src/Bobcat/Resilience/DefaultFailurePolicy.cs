using JasperFx.Testing;
using Bobcat.Engine;

namespace Bobcat.Resilience;

/// <summary>
/// The mapping from today's in-process failure model onto dispositions.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Retries are opt-in.</strong> A failure with no retry trait becomes
/// <see cref="DispositionKind.FailAndContinue"/>, never a retry. Retrying by default would
/// convert every genuine assertion failure into a slower genuine assertion failure, and — worse
/// — would make it impossible to tell a flaky test from a broken one. The author has to say
/// which tests are unreliable, and that statement is visible in the spec.
/// </para>
/// <para>
/// Ordering matters: catastrophic wins over everything, then the recycle/isolate traits (they
/// name a specific known cause), then plain <c>@retry(N)</c>.
/// </para>
/// </remarks>
public sealed class DefaultFailurePolicy : IFailurePolicy
{
    public Disposition? Decide(AttemptContext attempt)
    {
        if (attempt.Succeeded) return Disposition.Pass;

        // Catastrophic means nothing downstream can pass. Never retry it — the whole point is
        // that the environment is gone.
        if (attempt.FailureLevel == FailureLevel.Catastrophic ||
            attempt.Exception is SpecCatastrophicException)
        {
            return Disposition.AbortRun(
                attempt.Exception?.Message is { Length: > 0 } message
                    ? $"catastrophic failure: {message}"
                    : "catastrophic failure — nothing downstream can pass");
        }

        if (!attempt.RetriesAvailable)
        {
            // A test that asked to be retried and then ran out deserves to say so. Reporting a
            // bare "assertion failure" here would hide that the budget, not the test, ended it.
            if (!wantsRetry(attempt)) return Disposition.FailAndContinue(describeFailure(attempt));

            var exhausted = attempt.AttemptNumber >= attempt.AttemptsAllowed
                ? $"this test has used all {attempt.AttemptsAllowed} allowed attempts"
                : "the run's retry budget is exhausted";

            return Disposition.FailAndContinue($"{describeFailure(attempt)} — {exhausted}");
        }

        var resources = ResilienceTags.ParseResources(attempt.Trait(ResilienceTags.RecycleOnRetry));
        if (resources.Length > 0)
        {
            return Disposition.RetryAfterRecycle(
                $"tagged @recycle({string.Join(",", resources)}) — recycling before attempt {attempt.AttemptNumber + 1}",
                resources);
        }

        if (attempt.Trait(ResilienceTags.Isolated) == "true")
        {
            return Disposition.RetryInFreshProcess(
                $"tagged @isolated — attempt {attempt.AttemptNumber + 1} must run alone in a fresh process");
        }

        if (attempt.HasTrait(ResilienceTags.Retry))
        {
            return Disposition.RetryInProcess(
                $"tagged @retry — attempt {attempt.AttemptNumber + 1} of this test");
        }

        return Disposition.FailAndContinue(describeFailure(attempt));
    }

    /// <summary>True when the test opted into retrying at all.</summary>
    private static bool wantsRetry(AttemptContext attempt)
        => attempt.HasTrait(ResilienceTags.Retry)
           || attempt.HasTrait(ResilienceTags.RecycleOnRetry)
           || attempt.Trait(ResilienceTags.Isolated) == "true";

    private static string describeFailure(AttemptContext attempt)
        => attempt.FailureLevel switch
        {
            FailureLevel.Critical => "critical failure — scenario aborted",
            FailureLevel.Assertion => "assertion failure",
            _ => attempt.Exception is not null ? attempt.Exception.GetType().Name : "failed"
        };
}
