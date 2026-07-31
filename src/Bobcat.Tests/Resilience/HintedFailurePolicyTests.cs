using Bobcat.Engine;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Tests.Resilience;

public class HintedFailurePolicyTests
{
    private sealed class BrokerUnavailableException() : TimeoutException("no broker");

    [ClearsOnRetry(typeof(TimeoutException), Because = "the broker is slow to warm up")]
    [ClearsOnRecycle("rabbit", typeof(BrokerUnavailableException), Because = "the broker wedges")]
    [ClearsInFreshProcess(typeof(BadImageFormatException))]
    [NeverRecovers(typeof(NotSupportedException), Because = "this is a real bug")]
    private sealed class HintedFixture;

    private static readonly RecoveryHintSet Hints = new RecoveryHintSet().AddFromType(typeof(HintedFixture));

    private static readonly HintedFailurePolicy Policy = new(Hints);

    private static AttemptContext Attempt(
        Exception? exception = null,
        bool succeeded = false,
        bool retriesAvailable = true,
        FailureLevel level = FailureLevel.Critical,
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
    public void a_hinted_failure_is_retried_without_any_retry_tag()
    {
        // This is the point of layer 1: the author already knew this failure clears, and had to
        // tag the whole test @retry to say so — which also retried its genuine assertion failures.
        var disposition = Policy.Decide(Attempt(new TimeoutException()))!;

        disposition.Kind.ShouldBe(DispositionKind.RetryInProcess);
        disposition.Reason.ShouldContain("the broker is slow to warm up");
        disposition.Reason.ShouldContain("attempt 2");
    }

    [Fact]
    public void a_failure_no_hint_describes_is_left_to_the_default_policy()
    {
        // Abstaining rather than deciding is what lets the tag-driven default still apply.
        Policy.Decide(Attempt(new InvalidOperationException())).ShouldBeNull();
    }

    [Fact]
    public void a_passing_attempt_is_left_alone()
    {
        Policy.Decide(Attempt(succeeded: true, level: FailureLevel.None)).ShouldBeNull();
    }

    [Fact]
    public void an_empty_hint_set_never_decides_anything()
    {
        // A run with no hints must behave exactly as it did before this policy existed.
        new HintedFailurePolicy(new RecoveryHintSet())
            .Decide(Attempt(new TimeoutException()))
            .ShouldBeNull();
    }

    [Fact]
    public void a_recycle_hint_names_its_resources_on_the_disposition()
    {
        var disposition = Policy.Decide(Attempt(new BrokerUnavailableException()))!;

        disposition.Kind.ShouldBe(DispositionKind.RetryAfterRecycle);
        disposition.Resources.ShouldBe(["rabbit"]);
    }

    [Fact]
    public void a_fresh_process_hint_asks_for_one()
    {
        var disposition = Policy.Decide(Attempt(new BadImageFormatException()))!;

        disposition.Kind.ShouldBe(DispositionKind.RetryInFreshProcess);
        disposition.RequiresSupervisor.ShouldBeTrue();
    }

    [Fact]
    public void a_failure_declared_as_never_recovering_is_not_retried_even_when_the_test_is_tagged()
    {
        // The counterweight: @retry(3) on a test must not spend three attempts on a deterministic
        // bug the author already identified.
        var disposition = Policy.Decide(Attempt(new NotSupportedException(), tags: "retry(3)"))!;

        disposition.Kind.ShouldBe(DispositionKind.FailAndContinue);
        disposition.IsRetry.ShouldBeFalse();
        disposition.Reason.ShouldContain("this is a real bug");
        disposition.Reason.ShouldContain("never recovering");
    }

    [Fact]
    public void a_hint_never_overrides_a_catastrophic_failure()
    {
        // Nothing downstream can pass. The author's knowledge was about a test, not the world.
        Policy.Decide(Attempt(new TimeoutException(), level: FailureLevel.Catastrophic)).ShouldBeNull();
        Policy.Decide(Attempt(new SpecCatastrophicException("gone"))).ShouldBeNull();
    }

    [Fact]
    public void a_hint_does_not_widen_the_retry_budget()
    {
        // Knowledge of what recovers belongs to the test's author; how much time the run may
        // spend belongs to whoever runs it. A hint must never overrule the ceiling.
        var disposition = Policy.Decide(Attempt(new TimeoutException(), retriesAvailable: false))!;

        disposition.Kind.ShouldBe(DispositionKind.FailAndContinue);
        disposition.IsRetry.ShouldBeFalse();
    }

    [Fact]
    public void an_exhausted_budget_says_the_budget_stopped_it_rather_than_the_test()
    {
        // Reporting a bare failure here would hide that the fix is to raise the ceiling.
        var disposition = Policy.Decide(Attempt(
            new TimeoutException(), retriesAvailable: false, attemptNumber: 2, attemptsAllowed: 2))!;

        disposition.Reason.ShouldContain("used all 2 allowed attempts");
        disposition.Reason.ShouldContain("clears on retry");
    }

    [Fact]
    public void a_run_wide_budget_stopping_short_is_reported_as_the_run_not_the_test()
    {
        var disposition = Policy.Decide(Attempt(
            new TimeoutException(), retriesAvailable: false, attemptNumber: 1, attemptsAllowed: 5))!;

        disposition.Reason.ShouldContain("run's retry budget is exhausted");
    }

    [Fact]
    public void a_hint_matches_a_failure_type_reported_from_another_process()
    {
        // The supervisor never loads the worker's assembly, so all it has is a name.
        var attempt = new AttemptContext
        {
            TestId = "Feature/Scenario",
            Title = "Scenario",
            AttemptNumber = 1,
            Succeeded = false,
            FailureLevel = FailureLevel.Critical,
            RetriesAvailable = true,
            AttemptsAllowed = 2,
            Failure = FailureSignature.FromReportedType("TimeoutException", "timed out")
        };

        Policy.Decide(attempt)!.Kind.ShouldBe(DispositionKind.RetryInProcess);
    }

    [Fact]
    public void a_disposition_carries_the_hint_that_produced_it()
    {
        // Structural rather than parsed out of the reason, so a renderer can show a suppressed
        // retry without sniffing prose.
        Policy.Decide(Attempt(new TimeoutException()))!.Hint!.Because
            .ShouldBe("the broker is slow to warm up");

        Policy.Decide(Attempt(new NotSupportedException()))!.Hint!.Kind
            .ShouldBe(DispositionKind.FailAndContinue);
    }

    [Fact]
    public void a_tag_driven_decision_carries_no_hint()
    {
        new DefaultFailurePolicy().Decide(Attempt(new InvalidOperationException(), tags: "retry(2)"))!
            .Hint.ShouldBeNull();
    }

    [Fact]
    public void a_user_policy_still_outranks_a_hint()
    {
        // Hints sit between explicit code and the default. Code someone wrote for this run wins.
        var chain = new FailurePolicyChain(new AlwaysAbort(), Policy, new DefaultFailurePolicy());

        chain.Decide(Attempt(new TimeoutException()))!.Kind.ShouldBe(DispositionKind.AbortRun);
    }

    private sealed class AlwaysAbort : IFailurePolicy
    {
        public Disposition? Decide(AttemptContext attempt) => Disposition.AbortRun("because I said so");
    }
}
