using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Tests.Resilience;

public class ResilienceTagsTests
{
    [Fact]
    public void parse_resources_tolerates_empty_and_null()
    {
        ResilienceTags.ParseResources(null).ShouldBeEmpty();
        ResilienceTags.ParseResources("  ").ShouldBeEmpty();
    }
}

public class DispositionTests
{
    [Fact]
    public void recycle_without_resources_is_rejected_rather_than_silently_meaning_nothing()
    {
        Should.Throw<ArgumentException>(() => Disposition.RetryAfterRecycle("because"));
    }

    [Fact]
    public void retry_kinds_are_classified_correctly()
    {
        Disposition.RetryInProcess("x").IsRetry.ShouldBeTrue();
        Disposition.RetryInProcess("x").RequiresSupervisor.ShouldBeFalse();

        Disposition.RetryInFreshProcess("x").RequiresSupervisor.ShouldBeTrue();
        Disposition.RetryAfterRecycle("x", "rabbit").RequiresSupervisor.ShouldBeTrue();

        Disposition.Pass.IsRetry.ShouldBeFalse();
        Disposition.AbortRun("x").IsRetry.ShouldBeFalse();
        Disposition.FailAndContinue("x").IsRetry.ShouldBeFalse();
    }
}
