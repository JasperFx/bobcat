using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#569: each constant row of a data-driven spec is listed under its own identity, naming the
/// method's as its parent, and two rows that would share one are BOBCAT036.
/// </summary>
/// <remarks>
/// The runner attributes are declared in the source below, the shapes the real ones have, because
/// the generator reads them by name and constructor shape and references no test framework.
/// </remarks>
public class SpecRowIdentityTests
{
    private const string Runners =
        """
        using System;

        namespace Xunit
        {
            public sealed class InlineDataAttribute : Attribute
            {
                public InlineDataAttribute(params object?[]? data) { }
                public string? Label { get; set; }
            }

            public class TheoryAttribute : Attribute { }
            public sealed class MemberDataAttribute : Attribute { public MemberDataAttribute(string name) { } }
        }

        namespace Bobcat.Xunit
        {
            public sealed class BobcatExampleAttribute : Attribute
            {
                public BobcatExampleAttribute(string label, params object?[]? data) { }
            }

            public class BobcatTheoryAttribute : global::Xunit.TheoryAttribute
            {
                public BobcatTheoryAttribute() { }
                public BobcatTheoryAttribute(Type sliceType) { }
                public string? Name { get; set; }
            }
        }

        namespace NUnit.Framework
        {
            public sealed class TestCaseAttribute : Attribute
            {
                public TestCaseAttribute(params object?[]? arguments) { }
                public TestCaseAttribute(object? arg) { }
                public TestCaseAttribute(object? arg1, object? arg2) { }
            }
        }

        namespace Microsoft.VisualStudio.TestTools.UnitTesting
        {
            public sealed class TestMethodAttribute : Attribute { }
            public sealed class DataRowAttribute : Attribute
            {
                public DataRowAttribute(object? data) { }
                public DataRowAttribute(object? data, params object?[]? moreData) { }
            }
        }

        namespace TUnit.Core
        {
            public sealed class TestAttribute : Attribute { }
            public sealed class ArgumentsAttribute : Attribute
            {
                public ArgumentsAttribute(params object?[]? values) { }
            }
        }

        """;

    private static GeneratorHarness.RunOutcome run(string specs)
        => GeneratorHarness.Run(Runners +
                                """
                                namespace Specs
                                {
                                    using Xunit;
                                    using Bobcat.Xunit;

                                    public enum Tier { Silver, Gold }

                                """ + specs + "\n}");

    private static string registration(GeneratorHarness.RunOutcome outcome) => outcome.GeneratedSource("DeclaredSteps");

    [Fact]
    public void each_inline_row_is_bound_under_its_own_identity_naming_the_method_as_its_parent()
    {
        var outcome = run(
            """
            [BobcatFeature("Discounts")]
            public class DiscountSpecs
            {
                [BobcatTheory]
                [InlineData(100, Tier.Gold)]
                [InlineData(50, Tier.Silver)]
                public void discount_is_applied(int amount, Tier tier) { }
            }
            """);

        var source = registration(outcome);

        // The method's own identity stays: it is the specification the Event Model knows
        source.ShouldContain("""DeclaredSteps.Bind("Discounts/discount is applied", "Specs.DiscountSpecs", "discount_is_applied");""");

        source.ShouldContain(
            """DeclaredSteps.Bind("Discounts/discount is applied (amount: 100, tier: Gold)", "Specs.DiscountSpecs", "discount_is_applied", "Discounts/discount is applied");""");
        source.ShouldContain(
            """DeclaredSteps.Bind("Discounts/discount is applied (amount: 50, tier: Silver)", "Specs.DiscountSpecs", "discount_is_applied", "Discounts/discount is applied");""");

        outcome.WithId("BOBCAT036").ShouldBeEmpty();
    }

    [Fact]
    public void a_labelled_row_is_named_by_its_label()
    {
        var outcome = run(
            """
            [BobcatFeature("Discounts")]
            public class DiscountSpecs
            {
                [BobcatTheory]
                [BobcatExample("gold customer", 100, Tier.Gold)]
                [InlineData(50, Tier.Silver, Label = "silver customer")]
                [InlineData(10, Tier.Silver, Label = "")]
                public void discount_is_applied(int amount, Tier tier) { }
            }
            """);

        var source = registration(outcome);
        source.ShouldContain("\"Discounts/discount is applied [gold customer]\"");
        source.ShouldContain("\"Discounts/discount is applied [silver customer]\"");

        // An empty label is no label, as at run time
        source.ShouldContain("\"Discounts/discount is applied (amount: 10, tier: Silver)\"");
    }

    [Fact]
    public void values_are_formatted_the_way_the_runtime_formats_them()
    {
        var outcome = run(
            """
            [BobcatFeature("Values")]
            public class ValueSpecs
            {
                [Theory, BobcatScenario]
                [InlineData("a b", true, 1.5d, 'x', null, 7)]
                public void values(string text, bool flag, double ratio, char letter, string? missing, int? maybe) { }
            }
            """);

        registration(outcome).ShouldContain(
            "\"Values/values (text: a b, flag: true, ratio: 1.5, letter: x, missing: null, maybe: 7)\"");
    }

    [Fact]
    public void a_row_whose_identity_depends_on_a_runtime_conversion_or_data_is_not_listed()
    {
        var outcome = run(
            """
            [BobcatFeature("Unlisted")]
            public class UnlistedSpecs
            {
                // A runner converts this string to a Guid, whose text only the run knows
                [Theory, BobcatScenario]
                [InlineData("6F9619FF-8B86-D011-B42D-00CF4FC964FF")]
                public void a_guid(Guid id) { }

                // A params parameter collects the row however the runner decides
                [Theory, BobcatScenario]
                [InlineData(1, 2, 3)]
                public void many(params int[] values) { }

                // Rows only the run produces
                [Theory, BobcatScenario]
                [MemberData("Rows")]
                public void from_data(int value) { }
            }
            """);

        var source = registration(outcome);
        source.ShouldContain("""DeclaredSteps.Bind("Unlisted/a guid", "Specs.UnlistedSpecs", "a_guid");""");
        source.ShouldNotContain("Unlisted/a guid (");
        source.ShouldNotContain("Unlisted/many (");
        source.ShouldNotContain("Unlisted/from data (");
    }

    [Fact]
    public void nunit_mstest_and_tunit_constant_rows_are_listed_too()
    {
        var outcome = run(
            """
            [BobcatFeature("Runners")]
            public class RunnerSpecs
            {
                [NUnit.Framework.TestCase(1, "a"), BobcatScenario]
                public void nunit(int number, string word) { }

                [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod, BobcatScenario]
                [Microsoft.VisualStudio.TestTools.UnitTesting.DataRow(2, "b")]
                public void mstest(int number, string word) { }

                [TUnit.Core.Test, BobcatScenario]
                [TUnit.Core.Arguments(3, "c")]
                public void tunit(int number, string word) { }
            }
            """);

        var source = registration(outcome);
        source.ShouldContain("\"Runners/nunit (number: 1, word: a)\"");
        source.ShouldContain("\"Runners/mstest (number: 2, word: b)\"");
        source.ShouldContain("\"Runners/tunit (number: 3, word: c)\"");
    }

    [Fact]
    public void two_rows_that_format_alike_are_bobcat036_pointed_at_the_rows()
    {
        var outcome = run(
            """
            [BobcatFeature("Discounts")]
            public class DiscountSpecs
            {
                [BobcatTheory]
                [InlineData(100, Tier.Gold)]
                [InlineData(100, Tier.Gold)]
                [BobcatExample("vip", 1, Tier.Gold)]
                [BobcatExample("vip", 2, Tier.Silver)]
                public void discount_is_applied(int amount, Tier tier) { }
            }
            """);

        var errors = outcome.WithId("BOBCAT036").ToList();
        errors.Count.ShouldBe(4);
        errors.Count(x => x.GetMessage().Contains("'Discounts/discount is applied (amount: 100, tier: Gold)'")).ShouldBe(2);
        errors.Count(x => x.GetMessage().Contains("'Discounts/discount is applied [vip]'")).ShouldBe(2);
        errors.ShouldAllBe(x => x.GetMessage().Contains("[BobcatExample("));

        // At the row attribute, not the method name
        errors.ShouldAllBe(x => x.Location.SourceTree!.GetText(default).ToString(x.Location.SourceSpan).StartsWith("InlineData")
                                || x.Location.SourceTree!.GetText(default).ToString(x.Location.SourceSpan).StartsWith("BobcatExample"));
    }

    [Fact]
    public void bobcat_theory_is_a_test_that_opens_a_recording_and_binds_its_slice()
    {
        var outcome = run(
            """
            public record ApplyDiscount(int Amount);

            [BobcatFeature("Discounts")]
            public class DiscountSpecs
            {
                [BobcatTheory(typeof(ApplyDiscount), Name = "Discount is applied")]
                [InlineData(100)]
                public void discount(int amount)
                {
                    // Given a customer
                }
            }
            """);

        var source = registration(outcome);
        // The method and its row carry the slice's Event Model coordinates too (bobcat#573)
        source.ShouldContain("""DeclaredSteps.Bind("Discounts/Discount is applied", "Specs.DiscountSpecs", "discount", null, "ApplyDiscount", null, null, null);""");
        source.ShouldContain(
            """DeclaredSteps.Bind("Discounts/Discount is applied (amount: 100)", "Specs.DiscountSpecs", "discount", "Discounts/Discount is applied", "ApplyDiscount", null, null, null);""");

        // The declared steps are registered once, under the method: every row runs the same body
        source.ShouldContain("""DeclaredSteps.Register("Discounts/Discount is applied", """);
        source.ShouldNotContain("""DeclaredSteps.Register("Discounts/Discount is applied (""");

        // The positional slice type binds, as [BobcatSpec(typeof(X))] does, and nothing warns that
        // the slice records nothing
        outcome.WithId("BOBCAT028").ShouldBeEmpty();

        // One specification per method on the Event Model: the rows are examples of it
        var model = outcome.GeneratedSource("EventModel");
        model.ShouldContain("ApplyDiscount");
        model.ShouldContain("SpecificationDescriptor(\"Discounts/Discount is applied\",");
        model.ShouldNotContain("(amount: 100)");
    }
}
