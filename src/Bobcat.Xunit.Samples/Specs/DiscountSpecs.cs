using Bobcat.Xunit.Samples.Application;
using Shouldly;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The specifications behind <c>docs/spec-identities.md</c>, over <see cref="Discounts"/>. The theory's
/// three rows are each named a different way — by arguments, by xUnit's own <c>Label</c>, and by a
/// <c>[BobcatExample]</c> label — and the page quotes the identities the spec manifest lists for them.
/// All green.
/// </summary>
[BobcatFeature("Discounts")]
public class DiscountSpecs
{
    // #region row-identities
    [BobcatTheory(Name = "Discount is applied")]
    [InlineData(100, Tier.Gold, 80)]
    [InlineData(100, Tier.Silver, 90, Label = "silver customer")]
    [BobcatExample("nothing off for a standard customer", 100, Tier.Standard, 100)]
    public void discount(int amount, Tier tier, int total)
    {
        // Given an order for a customer of that tier
        // Then the tier's percentage comes off
        Discounts.Apply(amount, tier).ShouldBe(total);
    }
    // #endregion row-identities
}

/// <summary>
/// The page's opening example of a specification bound to an Event Model slice: the binding adds
/// coordinates beside the identity and changes nothing in it.
/// </summary>
// #region slice-bound
[BobcatFeature("Gold customers")]
public class GoldCustomerSpecs
{
    [BobcatSpec(SliceName = "Apply discount", Chapter = "Pricing", Domain = "Sales")]
    [Trait("Category", "Pricing")]
    public void a_gold_customer_gets_a_fifth_off()
    {
        // Given an order of 100 for a gold customer
        // Then 20 comes off
        Discounts.Apply(100, Tier.Gold).ShouldBe(80);
    }
}
// #endregion slice-bound

/// <summary>
/// The <c>docs/diagnostics.md</c> example of binding a slice by type rather than by name: the slice
/// is the type's name, and a rename follows it.
/// </summary>
// #region typeof-binding
[BobcatFeature("Applying discounts")]
public class ApplyDiscountSpecs
{
    [BobcatSpec(typeof(ApplyDiscount))]   // the slice is named ApplyDiscount, and nothing else says so
    public void a_silver_customer_gets_a_tenth_off()
    {
        // Given an order of 100 for a silver customer
        // Then 10 comes off
        Discounts.Apply(100, Tier.Silver).ShouldBe(90);
    }
}
// #endregion typeof-binding
