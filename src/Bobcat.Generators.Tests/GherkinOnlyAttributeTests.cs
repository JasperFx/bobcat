using Shouldly;
using Microsoft.CodeAnalysis;

namespace Bobcat.Generators.Tests;

/// <summary>
/// BOBCAT035: <c>[WaitFor]</c> and <c>[Approx]</c> are read only by the Gherkin generator, so in
/// projected code they silently do nothing. A helper carrying <c>[WaitFor(5000)]</c> ran once and
/// reported its first answer, which looked like a poll that happened to succeed straight away.
/// </summary>
public class GherkinOnlyAttributeTests
{
    private const string helperCalledFromATest =
        """
        using System.Threading.Tasks;
        using Bobcat;

        namespace Specs;

        public class OrderGrammar
        {
            [WaitFor(2000, PollAt = 10)]
            [Then("the outstanding count becomes {count}")]
            internal void OutstandingBecomes(int count) { }

            [Approx(0.1)]
            [Then("the average is {average}")]
            internal void AverageIs(double average) { }

            [Then("the total is {total}")]
            internal void TotalIs([Approx(0.01)] decimal total) { }
        }

        [BobcatFeature("Orders")]
        public class order_specs
        {
            private readonly OrderGrammar _grammar = new();

            public void first()
            {
                _grammar.OutstandingBecomes(0);
                _grammar.AverageIs(2.5);
                _grammar.TotalIs(10m);
            }

            public void second() => _grammar.OutstandingBecomes(1);
        }
        """;

    [Fact]
    public void a_wait_for_on_a_helper_a_test_calls_is_a_warning_pointing_at_the_poll_helper()
    {
        var diagnostics = GeneratorHarness.Run(helperCalledFromATest).WithId("BOBCAT035").ToList();

        // Once per method, not once per call site: OutstandingBecomes is called twice.
        var waitFor = diagnostics.Where(d => d.GetMessage().Contains("[WaitFor]")).ShouldHaveSingleItem();
        waitFor.Severity.ShouldBe(DiagnosticSeverity.Warning);
        waitFor.GetMessage().ShouldContain("'OutstandingBecomes'");
        waitFor.GetMessage().ShouldContain("SpecAssert.EventuallyAsync");
    }

    [Fact]
    public void an_approx_on_the_method_or_a_parameter_is_a_warning_pointing_at_the_tolerance_overload()
    {
        var approx = GeneratorHarness.Run(helperCalledFromATest).WithId("BOBCAT035")
            .Where(d => d.GetMessage().Contains("[Approx]"))
            .Select(d => d.GetMessage())
            .ToList();

        approx.Count.ShouldBe(2);
        approx.ShouldContain(m => m.Contains("'AverageIs'"));
        approx.ShouldContain(m => m.Contains("'TotalIs'"));
        approx.ShouldAllBe(m => m.Contains("tolerance:"));
    }

    [Fact]
    public void the_attributes_on_a_projected_spec_method_are_reported_too()
    {
        var diagnostic = GeneratorHarness.Run(
            """
            using Bobcat;

            namespace Specs;

            [BobcatFeature("Orders")]
            public class order_specs
            {
                [WaitFor(1000)]
                public void the_order_eventually_ships() { }
            }
            """).WithId("BOBCAT035").ShouldHaveSingleItem();

        diagnostic.GetMessage().ShouldContain("'the_order_eventually_ships'");
        diagnostic.Location.GetLineSpan().StartLinePosition.Line.ShouldBe(7);
    }

    [Fact]
    public void a_gherkin_binding_nothing_in_csharp_calls_is_left_alone()
    {
        // The Gherkin generator reads both attributes, so a fixture method bound only from a
        // .feature file is exactly where they belong.
        GeneratorHarness.Run(
            """
            using Bobcat;

            namespace Specs;

            public class OrderFixture : Fixture
            {
                [WaitFor(2000)]
                [Approx(0.1)]
                [Then("the average is {average}")]
                public double AverageIs() => 2.5;
            }
            """).WithId("BOBCAT035").ShouldBeEmpty();
    }

    [Fact]
    public void a_helper_that_polls_with_the_helper_reports_nothing()
    {
        GeneratorHarness.Run(
            """
            using System;
            using System.Threading.Tasks;
            using Bobcat;

            namespace Specs;

            public class OrderGrammar
            {
                [Then("the outstanding count becomes {count}")]
                internal Task OutstandingBecomes(int count)
                    => SpecAssert.EventuallyAsync("count", () => 0, count, TimeSpan.FromSeconds(2));
            }

            [BobcatFeature("Orders")]
            public class order_specs
            {
                public Task first() => new OrderGrammar().OutstandingBecomes(0);
            }
            """).WithId("BOBCAT035").ShouldBeEmpty();
    }
}
