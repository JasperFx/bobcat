using Bobcat.Generators;
using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// The closed comparison set, as the Shouldly dialect maps onto it (issue #384).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the whitelist is a test rather than a reading of the switch.</b> The enum being closed is
/// the whole mechanism: an assertion Bobcat has no member for produces <b>no cell</b>, so the honest
/// degradation to a plain step line is the only thing available. That makes the set of names which
/// map, and the set which deliberately do not, a contract — and a name quietly gaining a mapping
/// would start producing cells that state a comparison the renderer was never taught.
/// </para>
/// <para>
/// Tested through the pure function rather than through a compilation, because the mapping needs
/// exactly two facts — the method's name, and whether it takes a tolerance — and a generator harness
/// that had to supply a Shouldly reference and an MSBuild property would be testing the harness.
/// </para>
/// </remarks>
public class AssertionComparisonTests
{
    [Theory]
    [InlineData("ShouldBe", "Equals")]
    [InlineData("ShouldNotBe", "NotEquals")]
    [InlineData("ShouldBeGreaterThan", "GreaterThan")]
    [InlineData("ShouldBeGreaterThanOrEqualTo", "GreaterThanOrEqual")]
    [InlineData("ShouldBeLessThan", "LessThan")]
    [InlineData("ShouldBeLessThanOrEqualTo", "LessThanOrEqual")]
    [InlineData("ShouldContain", "Contains")]
    [InlineData("ShouldStartWith", "StartsWith")]
    [InlineData("ShouldEndWith", "EndsWith")]
    [InlineData("ShouldBeNull", "IsNull")]
    [InlineData("ShouldNotBeNull", "IsNotNull")]
    [InlineData("ShouldBeEmpty", "IsEmpty")]
    [InlineData("ShouldNotBeEmpty", "IsNotEmpty")]
    public void a_shouldly_assertion_maps_onto_the_comparison_it_makes(string method, string expected)
    {
        ShouldlyDialect.ComparisonFor(method, hasTolerance: false).ShouldBe(expected);
    }

    [Fact]
    public void should_be_with_a_tolerance_is_approximately_and_not_equality()
    {
        // The one case the method name alone cannot settle: ShouldBe(x) and ShouldBe(x, 0.01) are
        // the same name and different claims. Rendering the second as exact equality is the lie the
        // enum exists to make impossible, and it is the issue's own example.
        ShouldlyDialect.ComparisonFor("ShouldBe", hasTolerance: true).ShouldBe("Approximately");
        ShouldlyDialect.ComparisonFor("ShouldBe", hasTolerance: false).ShouldBe("Equals");
    }

    [Theory]
    [InlineData("ShouldBeTrue")]
    [InlineData("ShouldBeFalse")]
    [InlineData("ShouldBeEquivalentTo")]
    [InlineData("ShouldBeOfType")]
    [InlineData("ShouldThrow")]
    [InlineData("ShouldSatisfyAllConditions")]
    [InlineData("ShouldBeInRange")]
    public void an_assertion_outside_the_set_maps_to_nothing_so_no_cell_is_emitted(string method)
    {
        // Null here is what makes the generator pick the cell-less overload, which is acceptance 4
        // of #384: the step renders as a plain line with its verdict and duration, and says nothing
        // it cannot support. Each of these has an expectation that is not a value to put beside an
        // actual — ShouldBeTrue's subject IS the claim, ShouldBeInRange has two bounds — so any row
        // shape would have to state something false.
        ShouldlyDialect.ComparisonFor(method, hasTolerance: false).ShouldBeNull();
    }

    [Fact]
    public void every_mapped_name_names_a_real_member_of_the_closed_enum()
    {
        // The two halves are in different assemblies — the generator targets netstandard2.0 and
        // cannot reference the runtime — so a typo in the mapping would emit
        // `global::Bobcat.Engine.Comparison.GreatherThan` and break the CONSUMER's build in a
        // generated file they cannot open. This is the same class of hazard BOBCAT030 removed.
        string[] mapped =
        [
            "ShouldBe", "ShouldNotBe", "ShouldBeGreaterThan", "ShouldBeGreaterThanOrEqualTo",
            "ShouldBeLessThan", "ShouldBeLessThanOrEqualTo", "ShouldContain", "ShouldStartWith",
            "ShouldEndWith", "ShouldBeNull", "ShouldNotBeNull", "ShouldBeEmpty", "ShouldNotBeEmpty"
        ];

        var members = Enum.GetNames<Bobcat.Engine.Comparison>();

        foreach (var name in mapped)
        {
            var comparison = ShouldlyDialect.ComparisonFor(name, hasTolerance: false).ShouldNotBeNull();
            members.ShouldContain(comparison, $"'{name}' maps to '{comparison}', which is not a Comparison");
        }

        ShouldlyDialect.ComparisonFor("ShouldBe", hasTolerance: true).ShouldNotBeNull()
            .ShouldBeOneOf(members);
    }
}
