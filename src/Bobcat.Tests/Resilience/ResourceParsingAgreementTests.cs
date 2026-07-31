using Bobcat.Resilience;
using JasperFx.Testing;
using Shouldly;

namespace Bobcat.Tests.Resilience;

/// <summary>
/// Two implementations now parse the same comma-separated resource list, and neither depends on
/// the other.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ResilienceTags.ParseResources"/> reads the <c>@recycle(rabbit,kafka)</c> tag;
/// <see cref="ClearsOnRecycleAttribute"/> reads the same list off an attribute. The attribute lives
/// in JasperFx and deliberately does <em>not</em> reference Bobcat — that is the whole point of
/// issue #63, since it lets a test project annotate itself without taking a Bobcat dependency.
/// The cost of that independence is a duplicated three-line parse that can silently diverge.
/// </para>
/// <para>
/// Divergence would be quiet and nasty: a tag and a hint naming the same resources would resolve
/// to different names, so a recycle would either miss a broker or be reported as naming one nobody
/// registered. This pins them together, and the day someone changes either the failure names the
/// other.
/// </para>
/// </remarks>
public class ResourceParsingAgreementTests
{
    [Theory]
    [InlineData("rabbit")]
    [InlineData("rabbit,kafka")]
    [InlineData(" rabbit , kafka ")]
    [InlineData("rabbit,,kafka")]
    [InlineData("rabbit, ,kafka")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a,b,c,d")]
    public void the_tag_parser_and_the_attribute_agree(string declared)
    {
        var fromTag = ResilienceTags.ParseResources(declared);
        var fromAttribute = new ClearsOnRecycleAttribute(declared, typeof(TimeoutException)).Resources;

        fromAttribute.ShouldBe(fromTag);
    }

    [Fact]
    public void both_treat_nothing_as_no_resources_rather_than_one_blank_one()
    {
        // A runner asked to recycle "" would report a wiring mistake for a resource nobody meant
        // to name, which reads as a bug in the suite rather than a typo in a tag.
        ResilienceTags.ParseResources(null).ShouldBeEmpty();
        ResilienceTags.ParseResources("").ShouldBeEmpty();
        new ClearsOnRecycleAttribute("", typeof(TimeoutException)).Resources.ShouldBeEmpty();
        new ClearsOnRecycleAttribute("   ", typeof(TimeoutException)).Resources.ShouldBeEmpty();
    }
}
