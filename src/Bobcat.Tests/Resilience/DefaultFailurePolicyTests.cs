using JasperFx.Testing;
using Bobcat.Engine;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Tests.Resilience;

public class DefaultFailurePolicyTests
{
    private readonly DefaultFailurePolicy _policy = new();

    private static AttemptContext attempt(
        bool succeeded = false,
        FailureLevel level = FailureLevel.Assertion,
        Exception? exception = null,
        bool retriesAvailable = true,
        int attemptNumber = 1,
        int attemptsAllowed = 2,
        params string[] tags)
        => new()
        {
            TestId = "Feature/Scenario",
            Title = "Scenario",
            AttemptNumber = attemptNumber,
            Succeeded = succeeded,
            FailureLevel = level,
            Exception = exception,
            RetriesAvailable = retriesAvailable,
            AttemptsAllowed = attemptsAllowed,
            Traits = ResilienceTags.ToTraits(tags)
        };

    [Fact]
    public void a_passing_attempt_is_a_pass()
    {
        _policy.Decide(attempt(succeeded: true))!.Kind.ShouldBe(DispositionKind.Pass);
    }

    [Fact]
    public void an_untagged_failure_is_never_retried()
    {
        // Retries are opt-in. Retrying by default would turn every real assertion failure into
        // a slower real assertion failure, and make flaky indistinguishable from broken.
        var disposition = _policy.Decide(attempt(tags: []))!;

        disposition.Kind.ShouldBe(DispositionKind.FailAndContinue);
        disposition.Reason.ShouldBe("assertion failure");
    }

    [Fact]
    public void a_retry_tag_asks_for_an_in_process_retry()
    {
        var disposition = _policy.Decide(attempt(tags: "retry(2)"))!;

        disposition.Kind.ShouldBe(DispositionKind.RetryInProcess);
        disposition.IsRetry.ShouldBeTrue();
        disposition.RequiresSupervisor.ShouldBeFalse();
    }

    [Fact]
    public void an_isolated_tag_asks_for_a_fresh_process()
    {
        var disposition = _policy.Decide(attempt(tags: ["retry(2)", "isolated"]))!;

        disposition.Kind.ShouldBe(DispositionKind.RetryInFreshProcess);
        disposition.RequiresSupervisor.ShouldBeTrue();
    }

    [Fact]
    public void a_recycle_tag_names_the_resources_and_wins_over_isolated()
    {
        // Recycle names a specific known cause, so it outranks the more generic isolation ask.
        var disposition = _policy.Decide(attempt(tags: ["isolated", "recycle(rabbit,kafka)"]))!;

        disposition.Kind.ShouldBe(DispositionKind.RetryAfterRecycle);
        disposition.Resources.ShouldBe(["rabbit", "kafka"]);
    }

    [Fact]
    public void catastrophic_aborts_the_run_and_is_never_retried()
    {
        var disposition = _policy.Decide(attempt(
            level: FailureLevel.Catastrophic,
            tags: "retry(5)"))!;

        disposition.Kind.ShouldBe(DispositionKind.AbortRun);
        disposition.IsRetry.ShouldBeFalse();
    }

    [Fact]
    public void a_catastrophic_exception_aborts_even_when_the_level_was_not_set()
    {
        var disposition = _policy.Decide(attempt(
            level: FailureLevel.None,
            exception: new SpecCatastrophicException("the database is gone"),
            tags: "retry(5)"))!;

        disposition.Kind.ShouldBe(DispositionKind.AbortRun);
        disposition.Reason.ShouldContain("the database is gone");
    }

    [Fact]
    public void an_exhausted_budget_downgrades_a_retry_request_to_a_plain_failure()
    {
        var disposition = _policy.Decide(attempt(retriesAvailable: false, tags: "retry(2)"))!;

        disposition.Kind.ShouldBe(DispositionKind.FailAndContinue);
    }

    [Fact]
    public void a_test_that_ran_out_of_attempts_says_so_rather_than_reporting_a_bare_failure()
    {
        // "assertion failure" alone would hide that the budget, not the test, ended the retrying.
        var disposition = _policy.Decide(attempt(
            retriesAvailable: false, attemptNumber: 3, attemptsAllowed: 3, tags: "retry(3)"))!;

        disposition.Reason.ShouldBe("assertion failure — this test has used all 3 allowed attempts");
    }

    [Fact]
    public void a_run_wide_exhaustion_is_distinguished_from_a_per_test_one()
    {
        // Still on attempt 1 of an allowed 3, so it was the RUN's ceiling that stopped this.
        var disposition = _policy.Decide(attempt(
            retriesAvailable: false, attemptNumber: 1, attemptsAllowed: 3, tags: "retry(3)"))!;

        disposition.Reason.ShouldBe("assertion failure — the run's retry budget is exhausted");
    }

    [Fact]
    public void an_untagged_test_with_no_retries_left_is_not_told_about_a_budget_it_never_used()
    {
        var disposition = _policy.Decide(attempt(retriesAvailable: false, tags: []))!;

        disposition.Reason.ShouldBe("assertion failure");
    }

    [Fact]
    public void a_critical_failure_is_described_as_such()
    {
        _policy.Decide(attempt(level: FailureLevel.Critical))!
            .Reason.ShouldBe("critical failure — scenario aborted");
    }

    [Fact]
    public void a_custom_policy_decides_before_the_default_and_may_abstain()
    {
        var chain = new FailurePolicyChain(new AbstainUnlessTagged(), new DefaultFailurePolicy());

        chain.Decide(attempt(tags: "quarantine"))!.Kind.ShouldBe(DispositionKind.RetryInProcess);
        // Abstained — the default answers instead.
        chain.Decide(attempt(tags: []))!.Kind.ShouldBe(DispositionKind.FailAndContinue);
    }

    private class AbstainUnlessTagged : IFailurePolicy
    {
        public Disposition? Decide(AttemptContext attempt)
            => attempt.HasTrait("quarantine")
                ? Disposition.RetryInProcess("quarantined test")
                : null;
    }
}
