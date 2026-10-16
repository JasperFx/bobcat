using System.Reflection;
using Shouldly;
using Xunit;
using Xunit.v3;

namespace Bobcat.Xunit.Tests;

/// <summary>
/// bobcat#569: <c>[BobcatTheory]</c> is the <c>[Theory]</c> and the scenario bracket at once, and
/// each row records under its own identity — pinned against the real runner, because the row's
/// arguments and label reach the bracket through xUnit's <c>IXunitTest</c>, not through anything
/// Bobcat controls.
/// </summary>
[BobcatFeature("Discounts")]
public class BobcatTheoryAttributeTests
{
    public enum Tier { Silver, Gold }

    [BobcatTheory]
    [InlineData(100, Tier.Gold)]
    [InlineData(50, Tier.Silver)]
    public void each_row_records_under_its_own_identity(int amount, Tier tier)
    {
        var recording = ScenarioRecorder.Current.ShouldNotBeNull(
            "a running body proves discovery, and an open recording proves the implied scenario");

        recording.Uid.ShouldBe($"Discounts/each row records under its own identity (amount: {amount}, tier: {tier})");
        recording.ParentUid.ShouldBe("Discounts/each row records under its own identity");
    }

    [BobcatTheory]
    [BobcatExample("gold customer", 100, Tier.Gold)]
    [InlineData(50, Tier.Silver, Label = "silver customer")]
    public void a_labelled_row_is_named_by_its_label(int amount, Tier tier)
    {
        var label = tier == Tier.Gold ? "gold customer" : "silver customer";

        ScenarioRecorder.Current.ShouldNotBeNull()
            .Uid.ShouldBe($"Discounts/a labelled row is named by its label [{label}]");

        amount.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// A plain <c>[Theory]</c> under the class-level opt-in is data-driven too, so it gets row
    /// identities without the one-attribute form.
    /// </summary>
    [Theory, BobcatScenario]
    [InlineData("plain")]
    public void a_plain_theory_rows_are_named_too(string word)
        => ScenarioRecorder.Current.ShouldNotBeNull()
            .Uid.ShouldBe($"Discounts/a plain theory rows are named too (word: {word})");

    [BobcatTheory(Name = "Discount is applied, explicitly")]
    [InlineData(1)]
    public void the_explicit_name_is_the_method_part_of_the_row(int amount)
        => ScenarioRecorder.Current.ShouldNotBeNull()
            .Uid.ShouldBe($"Discounts/Discount is applied, explicitly (amount: {amount})");

    [Fact]
    public void the_caller_file_points_at_the_test_not_at_the_attribute()
    {
        var theory = typeof(BobcatTheoryAttributeTests)
            .GetMethod(nameof(each_row_records_under_its_own_identity))!
            .GetCustomAttribute<BobcatTheoryAttribute>()!;

        Path.GetFileName(theory.SourceFilePath).ShouldBe("BobcatTheoryAttributeTests.cs");
    }

    [Fact]
    public void it_is_a_theory_and_a_before_after_hook()
    {
        typeof(BobcatTheoryAttribute).IsSubclassOf(typeof(TheoryAttribute)).ShouldBeTrue();
        typeof(IBeforeAfterTestAttribute).IsAssignableFrom(typeof(BobcatTheoryAttribute)).ShouldBeTrue();
    }

    [Fact]
    public void the_positional_constructor_sets_the_slice_type()
        => new BobcatTheoryAttribute(typeof(BobcatTheoryAttributeTests))
            .SliceType.ShouldBe(typeof(BobcatTheoryAttributeTests));

    [Fact]
    public void pending_skips_the_theory()
        => new BobcatTheoryAttribute { Pending = true }.Skip.ShouldBe(BobcatSpecAttribute.PendingSkipReason);
}
