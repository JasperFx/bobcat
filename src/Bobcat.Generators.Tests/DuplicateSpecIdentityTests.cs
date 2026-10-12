using Microsoft.CodeAnalysis;
using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#489: two projected specs that resolve to the same <c>{Feature}/{Scenario}</c> identity are
/// one specification to everything keyed on it, so the generator refuses them as BOBCAT036 -- and
/// <c>[BobcatSpec(Name = "…")]</c> is the explicit scenario name that tells them apart.
/// </summary>
public class DuplicateSpecIdentityTests
{
    private const string Preamble =
        """
        using System;
        using Xunit;

        namespace Specs;

        """;

    [Fact]
    public void same_named_methods_in_two_classes_bound_to_one_feature_collide()
    {
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Ordering")]
            public class OrderingSpecs
            {
                [BobcatSpec]
                public void an_order_is_accepted() { }
            }

            [BobcatFeature("Ordering")]
            public class MoreOrderingSpecs
            {
                [BobcatSpec]
                public void an_order_is_accepted() { }
            }
            """);

        var errors = outcome.WithId("BOBCAT036").ToList();

        // Reported at BOTH claimants, each naming the other, so whichever file is open says what it
        // collides with
        errors.Count.ShouldBe(2);
        errors.ShouldAllBe(x => x.Severity == DiagnosticSeverity.Error);

        var first = errors.Single(x => x.GetMessage().Contains("Specs.MoreOrderingSpecs.an_order_is_accepted"));
        var second = errors.Single(x => x.GetMessage().Contains("Specs.OrderingSpecs.an_order_is_accepted"));
        first.ShouldNotBeSameAs(second);

        first.GetMessage().ShouldContain("'Ordering/an order is accepted'");
        first.GetMessage().ShouldContain("[BobcatSpec(Name = \"...\")]");
        first.AdditionalLocations.ShouldHaveSingleItem();

        // Each one points at its own method name
        locationText(first).ShouldBe("an_order_is_accepted");
        first.Location.ShouldNotBe(second.Location);
    }

    [Fact]
    public void the_collision_is_reported_with_the_file_and_line_of_the_other_claimant()
    {
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Ordering")]
            public class OrderingSpecs
            {
                [Fact, BobcatScenario]
                public void an_order_is_accepted() { }

                [Fact, BobcatScenario]
                public void an_order__is_accepted() { }
            }
            """);

        // Two method names that read as the same sentence ARE the same identity: underscores are
        // spaces and empty segments drop, exactly as the runtime derives it
        var errors = outcome.WithId("BOBCAT036").ToList();
        errors.Count.ShouldBe(2);
        errors.ShouldContain(x => x.GetMessage().Contains("Specs.OrderingSpecs.an_order__is_accepted (") &&
                                  x.GetMessage().Contains(":12)"));
    }

    [Fact]
    public void distinct_names_do_not_collide()
    {
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Ordering")]
            public class OrderingSpecs
            {
                [BobcatSpec]
                public void an_order_is_accepted() { }

                [BobcatSpec]
                public void an_order_is_rejected() { }
            }

            // The same method name under a DIFFERENT feature is a different identity
            [BobcatFeature("Shipping")]
            public class ShippingSpecs
            {
                [BobcatSpec]
                public void an_order_is_accepted() { }
            }
            """);

        outcome.WithId("BOBCAT036").ShouldBeEmpty();
    }

    [Fact]
    public void identities_differing_only_in_case_are_two_specifications()
    {
        // Ordinal and exact, the way SpecIdentity compares at run time. Folding case here would
        // refuse two identities that never collide anywhere downstream.
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Ordering")]
            public class OrderingSpecs
            {
                [BobcatSpec]
                public void an_order_is_accepted() { }

                [BobcatSpec]
                public void An_Order_Is_Accepted() { }
            }
            """);

        outcome.WithId("BOBCAT036").ShouldBeEmpty();
    }

    [Fact]
    public void an_explicit_name_resolves_the_collision()
    {
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Ordering")]
            public class OrderingSpecs
            {
                [BobcatSpec]
                public void an_order_is_accepted() { }
            }

            [BobcatFeature("Ordering")]
            public class MoreOrderingSpecs
            {
                [BobcatSpec(Name = "an order is accepted a second time")]
                public void an_order_is_accepted() { }
            }
            """);

        outcome.WithId("BOBCAT036").ShouldBeEmpty();

        var registration = outcome.GeneratedSource("DeclaredSteps");
        registration.ShouldContain(
            """DeclaredSteps.Bind("Ordering/an order is accepted", "Specs.OrderingSpecs", "an_order_is_accepted")""");
        registration.ShouldContain(
            """DeclaredSteps.Bind("Ordering/an order is accepted a second time", "Specs.MoreOrderingSpecs", "an_order_is_accepted")""");
    }

    [Fact]
    public void an_explicit_name_that_matches_another_specs_derived_title_still_collides()
    {
        // The check compares the identities that get EMITTED, whichever way each title was arrived at
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Ordering")]
            public class OrderingSpecs
            {
                [BobcatSpec]
                public void an_order_is_accepted() { }

                [BobcatSpec(Name = "an order is accepted")]
                public void accepted() { }
            }
            """);

        outcome.WithId("BOBCAT036").Count().ShouldBe(2);
    }

    [Fact]
    public void an_explicit_name_keeps_the_punctuation_a_method_name_cannot_spell()
    {
        var outcome = GeneratorHarness.Run(Preamble +
            """
            public record ConfirmAppointment(Guid AppointmentId);

            [BobcatFeature("Booking")]
            public class BookingSpecs
            {
                [BobcatSpec(typeof(ConfirmAppointment), Name = "A volunteer can't re-confirm, \"twice\"")]
                public void volunteer_cannot_reconfirm()
                {
                    // Given a confirmed appointment
                }
            }
            """);

        outcome.WithId("BOBCAT036").ShouldBeEmpty();

        const string escaped = "\"Booking/A volunteer can't re-confirm, \\\"twice\\\"\"";

        var registration = outcome.GeneratedSource("DeclaredSteps");
        registration.ShouldContain($"DeclaredSteps.Bind({escaped}, \"Specs.BookingSpecs\", \"volunteer_cannot_reconfirm\")");
        registration.ShouldContain($"DeclaredSteps.Register({escaped},");

        // And the Event Model's spec list carries the same string, so run evidence joins the slice
        outcome.GeneratedSource("EventModel").ShouldContain($"SpecificationDescriptor({escaped},");

        // The generated source still compiles with the quotes in it
        outcome.CompilationErrors
            .Where(x => x.Location.SourceTree?.FilePath.Contains("Bobcat", StringComparison.Ordinal) == true)
            .ShouldBeEmpty();
    }

    [Fact]
    public void an_explicit_name_can_be_a_constant()
    {
        var outcome = GeneratorHarness.Run(Preamble +
            """
            public static class Names
            {
                public const string Twice = "it's accepted twice";
            }

            [BobcatFeature("Ordering")]
            public class OrderingSpecs
            {
                [BobcatSpec(Name = Names.Twice)]
                public void accepted() { }
            }
            """);

        outcome.GeneratedSource("DeclaredSteps")
            .ShouldContain("""DeclaredSteps.Bind("Ordering/it's accepted twice", "Specs.OrderingSpecs", "accepted")""");
    }

    [Fact]
    public void a_blank_explicit_name_is_no_name_and_the_identity_is_unchanged()
    {
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Ordering")]
            public class OrderingSpecs
            {
                [BobcatSpec(Name = "  ")]
                public void an_order_is_accepted() { }
            }
            """);

        outcome.GeneratedSource("DeclaredSteps")
            .ShouldContain("""DeclaredSteps.Bind("Ordering/an order is accepted", "Specs.OrderingSpecs", "an_order_is_accepted")""");
    }

    private static string locationText(Diagnostic diagnostic)
        => diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan);
}
