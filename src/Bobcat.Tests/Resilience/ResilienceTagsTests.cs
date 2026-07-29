using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Tests.Resilience;

public class ResilienceTagsTests
{
    [Fact]
    public void retry_tag_becomes_the_retry_trait()
    {
        ResilienceTags.ToTraits(["retry(3)"])[ResilienceTags.Retry].ShouldBe("3");
    }

    [Fact]
    public void isolated_tag_becomes_a_boolean_trait()
    {
        ResilienceTags.ToTraits(["isolated"])[ResilienceTags.Isolated].ShouldBe("true");
    }

    [Fact]
    public void recycle_tag_carries_its_resource_list()
    {
        var traits = ResilienceTags.ToTraits(["recycle(rabbit, kafka)"]);

        ResilienceTags.ParseResources(traits[ResilienceTags.RecycleOnRetry])
            .ShouldBe(["rabbit", "kafka"]);
    }

    [Fact]
    public void an_unrecognized_tag_still_becomes_a_trait_so_custom_policies_can_key_off_it()
    {
        var traits = ResilienceTags.ToTraits(["slow", "regression"]);

        traits["slow"].ShouldBe("true");
        traits["regression"].ShouldBe("true");
    }

    [Fact]
    public void traits_are_matched_case_insensitively()
    {
        // A Gherkin author writing @Isolated and an xUnit author writing [Trait("isolated",…)]
        // must reach the same policy branch.
        var traits = ResilienceTags.ToTraits(["ISOLATED"]);

        traits.ContainsKey("isolated").ShouldBeTrue();
        traits.ContainsKey("Isolated").ShouldBeTrue();
    }

    [Fact]
    public void a_malformed_argument_tag_is_treated_as_a_plain_tag_not_an_error()
    {
        var traits = ResilienceTags.ToTraits(["retry()", "recycle("]);

        traits["retry()"].ShouldBe("true");
        traits["recycle("].ShouldBe("true");
        traits.ContainsKey(ResilienceTags.Retry).ShouldBeFalse();
    }

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
