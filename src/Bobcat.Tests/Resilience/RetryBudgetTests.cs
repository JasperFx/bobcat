using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Tests.Resilience;

public class RetryBudgetTests
{
    private static readonly IReadOnlyDictionary<string, string> noTraits =
        new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, string> traits(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void none_allows_a_single_attempt_and_no_retries()
    {
        var budget = RetryBudget.None;

        budget.AttemptsAllowedFor(noTraits).ShouldBe(1);
        budget.CanRetry("a", noTraits).ShouldBeFalse();
        budget.TryConsume("a", noTraits, out var denial).ShouldBeFalse();
        denial.ShouldBe("retries are not enabled for this test");
    }

    [Fact]
    public void max_attempts_of_two_permits_exactly_one_retry()
    {
        var budget = new RetryBudget { MaxAttemptsPerTest = 2 };

        budget.TryConsume("a", noTraits, out _).ShouldBeTrue();
        budget.TryConsume("a", noTraits, out var denial).ShouldBeFalse();

        denial.ShouldBe("this test has used all 2 allowed attempts");
        budget.RetriesSpent.ShouldBe(1);
    }

    [Fact]
    public void the_per_test_ceiling_is_tracked_independently_for_each_test()
    {
        var budget = new RetryBudget { MaxAttemptsPerTest = 2 };

        budget.TryConsume("a", noTraits, out _).ShouldBeTrue();
        budget.TryConsume("b", noTraits, out _).ShouldBeTrue();

        budget.RetriesSpent.ShouldBe(2);
    }

    [Fact]
    public void a_retry_tag_may_lower_the_allowance()
    {
        var budget = new RetryBudget { MaxAttemptsPerTest = 5 };

        budget.AttemptsAllowedFor(traits((ResilienceTags.Retry, "2"))).ShouldBe(2);
    }

    [Fact]
    public void a_retry_tag_may_NOT_raise_the_allowance_past_the_run_ceiling()
    {
        // The run-level ceiling is what an operator sets. A spec author must not be able to
        // escape it by asking for more in a tag.
        var budget = new RetryBudget { MaxAttemptsPerTest = 2 };

        budget.AttemptsAllowedFor(traits((ResilienceTags.Retry, "99"))).ShouldBe(2);
    }

    [Fact]
    public void the_run_wide_ceiling_stops_a_broadly_broken_environment()
    {
        // Every test is failing and each asks for a retry — the run cap is what keeps that from
        // burning the whole CI slot.
        var budget = new RetryBudget { MaxAttemptsPerTest = 5, MaxRetriesPerRun = 2 };

        budget.TryConsume("a", noTraits, out _).ShouldBeTrue();
        budget.TryConsume("b", noTraits, out _).ShouldBeTrue();
        budget.TryConsume("c", noTraits, out var denial).ShouldBeFalse();

        denial.ShouldBe("the run's retry budget is exhausted (2 retries)");
        budget.CanRetry("d", noTraits).ShouldBeFalse();
    }

    [Fact]
    public void a_denied_retry_spends_nothing()
    {
        var budget = new RetryBudget { MaxAttemptsPerTest = 1 };

        budget.TryConsume("a", noTraits, out _).ShouldBeFalse();

        budget.RetriesSpent.ShouldBe(0);
    }

    [Fact]
    public void a_nonsense_retry_tag_falls_back_to_the_run_ceiling()
    {
        var budget = new RetryBudget { MaxAttemptsPerTest = 3 };

        budget.AttemptsAllowedFor(traits((ResilienceTags.Retry, "not-a-number"))).ShouldBe(3);
        budget.AttemptsAllowedFor(traits((ResilienceTags.Retry, "0"))).ShouldBe(3);
    }
}
