using Bobcat.Resilience;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// The feature-level slice vocabulary (#104): the only non-derivable bits of an Event Modeling
/// slice — its name, its domain, and the trigger — parsed off a feature's tags and description and
/// projected onto traits so #106 can read them without a Bobcat reference.
/// </summary>
public class SliceTagsTests
{
    [Fact]
    public void reads_slice_and_domain_from_tags()
    {
        var tags = new[] { "slice:WithdrawFunds", "domain:BankAccount", "regression" };

        SliceTags.Slice(tags).ShouldBe("WithdrawFunds");
        SliceTags.Domain(tags).ShouldBe("BankAccount");
    }

    [Fact]
    public void missing_slice_or_domain_is_null()
    {
        SliceTags.Slice(["regression"]).ShouldBeNull();
        SliceTags.Domain([]).ShouldBeNull();
    }

    [Theory]
    [InlineData("Triggered by the account holder", "the account holder")]
    [InlineData("Triggered by: a scheduled job", "a scheduled job")]
    [InlineData("Some prose\nTriggered by an operator", "an operator")]
    public void reads_the_trigger_from_the_description(string description, string expected)
        => SliceTags.TriggeredBy(description).ShouldBe(expected);

    [Fact]
    public void no_trigger_line_is_null()
        => SliceTags.TriggeredBy("Just a plain description").ShouldBeNull();

    [Fact]
    public void key_value_tags_project_onto_traits()
    {
        // A slice/domain tag becomes a readable trait, alongside the recognised retry vocabulary.
        var traits = ResilienceTags.ToTraits(["slice:WithdrawFunds", "domain:BankAccount", "isolated"]);

        traits["Slice"].ShouldBe("WithdrawFunds");
        traits["Domain"].ShouldBe("BankAccount");
        traits[ResilienceTags.Isolated].ShouldBe("true");
    }

    [Fact]
    public void a_plain_tag_still_projects_as_true()
        => ResilienceTags.ToTraits(["smoke"])["smoke"].ShouldBe("true");
}
