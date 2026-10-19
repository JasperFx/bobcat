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
        params string[] traits)
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
            Traits = TestTraits.Of(traits)
        };

    [Fact]
    public void a_passing_attempt_is_a_pass()
    {
        _policy.Decide(attempt(succeeded: true))!.Kind.ShouldBe(DispositionKind.Pass);
    }

    [Fact]
    public void a_failure_without_retry_traits_is_never_retried()
    {
        // Retries are opt-in. Retrying by default would turn every real assertion failure into
        // a slower real assertion failure, and make flaky indistinguishable from broken.
        var disposition = _policy.Decide(attempt(traits: []))!;

        disposition.Kind.ShouldBe(DispositionKind.FailAndContinue);
        disposition.Reason.ShouldBe("assertion failure");
    }

    [Fact]
    public void a_retry_trait_asks_for_a_fresh_process_retry()
    {
        // bobcat#661: a failure that polluted its process fails every warm retry with the first
        // attempt's leftovers, and the fresh process is the only reset bracket a worker has.
        var disposition = _policy.Decide(attempt(traits: "Retry=2"))!;

        disposition.Kind.ShouldBe(DispositionKind.RetryInFreshProcess);
        disposition.IsRetry.ShouldBeTrue();
        disposition.RequiresSupervisor.ShouldBeTrue();
    }

    [Fact]
    public void a_warm_retry_is_an_explicit_opt_in()
    {
        var disposition = _policy.Decide(attempt(traits: ["Retry=2", "RetryWarm=true"]))!;

        disposition.Kind.ShouldBe(DispositionKind.RetryInProcess);
        disposition.RequiresSupervisor.ShouldBeFalse();
        disposition.Reason.ShouldContain("warm");
    }

    [Fact]
    public void an_isolated_trait_asks_for_a_fresh_process()
    {
        var disposition = _policy.Decide(attempt(traits: ["Retry=2", "Isolated=true"]))!;

        disposition.Kind.ShouldBe(DispositionKind.RetryInFreshProcess);
        disposition.RequiresSupervisor.ShouldBeTrue();
    }

    [Fact]
    public void a_recycle_trait_names_the_resources_and_wins_over_isolated()
    {
        // Recycle names a specific known cause, so it outranks the more generic isolation ask.
        var disposition = _policy.Decide(attempt(traits: ["Isolated=true", "RecycleOnRetry=rabbit,kafka"]))!;

        disposition.Kind.ShouldBe(DispositionKind.RetryAfterRecycle);
        disposition.Resources.ShouldBe(["rabbit", "kafka"]);
    }

    [Fact]
    public void catastrophic_aborts_the_run_and_is_never_retried()
    {
        var disposition = _policy.Decide(attempt(
            level: FailureLevel.Catastrophic,
            traits: "Retry=5"))!;

        disposition.Kind.ShouldBe(DispositionKind.AbortRun);
        disposition.IsRetry.ShouldBeFalse();
    }

    [Fact]
    public void a_catastrophic_exception_aborts_even_when_the_level_was_not_set()
    {
        var disposition = _policy.Decide(attempt(
            level: FailureLevel.None,
            exception: new SpecCatastrophicException("the database is gone"),
            traits: "Retry=5"))!;

        disposition.Kind.ShouldBe(DispositionKind.AbortRun);
        disposition.Reason.ShouldContain("the database is gone");
    }

    [Fact]
    public void an_exhausted_budget_downgrades_a_retry_request_to_a_plain_failure()
    {
        var disposition = _policy.Decide(attempt(retriesAvailable: false, traits: "Retry=2"))!;

        disposition.Kind.ShouldBe(DispositionKind.FailAndContinue);
    }

    [Fact]
    public void a_test_that_ran_out_of_attempts_says_so_rather_than_reporting_a_bare_failure()
    {
        // "assertion failure" alone would hide that the budget, not the test, ended the retrying.
        var disposition = _policy.Decide(attempt(
            retriesAvailable: false, attemptNumber: 3, attemptsAllowed: 3, traits: "Retry=3"))!;

        disposition.Reason.ShouldBe("assertion failure — this test has used all 3 allowed attempts");
    }

    [Fact]
    public void a_run_wide_exhaustion_is_distinguished_from_a_per_test_one()
    {
        // Still on attempt 1 of an allowed 3, so it was the RUN's ceiling that stopped this.
        var disposition = _policy.Decide(attempt(
            retriesAvailable: false, attemptNumber: 1, attemptsAllowed: 3, traits: "Retry=3"))!;

        disposition.Reason.ShouldBe("assertion failure — the run's retry budget is exhausted");
    }

    [Fact]
    public void a_test_without_retry_traits_and_no_retries_left_is_not_told_about_a_budget_it_never_used()
    {
        var disposition = _policy.Decide(attempt(retriesAvailable: false, traits: []))!;

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
        var chain = new FailurePolicyChain(new AbstainUnlessQuarantined(), new DefaultFailurePolicy());

        chain.Decide(attempt(traits: "quarantine=true"))!.Kind.ShouldBe(DispositionKind.RetryInProcess);
        // Abstained — the default answers instead.
        chain.Decide(attempt(traits: []))!.Kind.ShouldBe(DispositionKind.FailAndContinue);
    }

    private class AbstainUnlessQuarantined : IFailurePolicy
    {
        public Disposition? Decide(AttemptContext attempt)
            => attempt.HasTrait("quarantine")
                ? Disposition.RetryInProcess("quarantined test")
                : null;
    }
}
