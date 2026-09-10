using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// Issue #273's secondary note, isolated: following BOBCAT001's own advice did not clear
/// BOBCAT001.
/// </summary>
/// <remarks>
/// The reporter recorded it as an observation because they could not pin it down, and guessed at
/// <c>CritterStackFixture</c> + a namespace. Neither is involved. The message built its suggested
/// class name by pasting "Fixture" onto the feature title, while convention matching runs the
/// other way — class name minus "Fixture", then split on camel humps. Those are inverses only when
/// the title's spaces already sit exactly where the humps are.
///
/// A one-word PascalCase title is the common case where they are not: <c>BookingShipments</c> was
/// told to name its class <c>BookingShipmentsFixture</c>, which derives to "Booking Shipments" and
/// matches nothing. A title with a space fails differently and worse — <c>Wallet over HTTP</c> was
/// told to write <c>Wallet over HTTPFixture</c>, which is not a legal identifier.
/// </remarks>
public class NoMatchingFixtureAdviceTests
{
    private static string adviceFor(string featureTitle, string fixtureSource)
        => GeneratorHarness.Run(fixtureSource, ("X.feature", $"Feature: {featureTitle}\n\n  Scenario: S\n    Given nothing\n"))
            .WithId("BOBCAT001")
            .Select(d => d.GetMessage())
            .FirstOrDefault() ?? "";

    private const string NoFixture =
        """
        using Bobcat;

        namespace Specs;

        public class Unrelated : Fixture
        {
            [Given("nothing")]
            public void GivenNothing() { }
        }
        """;

    [Fact]
    public void a_one_word_title_is_not_offered_a_class_name_that_cannot_match_it()
    {
        var advice = adviceFor("BookingShipments", NoFixture);

        // The reporter was told to write this, wrote it, and the diagnostic stayed — because it
        // derives back to "Booking Shipments". No class name can match a title like this one.
        advice.ShouldNotContain("BookingShipmentsFixture");
        advice.ShouldContain("No class name derives to this title by convention");
        advice.ShouldContain("[FixtureTitle(\"BookingShipments\")]");
    }

    [Fact]
    public void following_the_advice_clears_the_diagnostic()
    {
        // The property the old message broke: whatever it tells you to do has to work.
        const string followed =
            """
            using Bobcat;

            namespace Specs;

            [FixtureTitle("BookingShipments")]
            public class BookingShipmentsFixture : Fixture
            {
                [Given("nothing")]
                public void GivenNothing() { }
            }
            """;

        var outcome = GeneratorHarness.Run(followed,
            ("X.feature", "Feature: BookingShipments\n\n  Scenario: S\n    Given nothing\n"));

        outcome.WithId("BOBCAT001").ShouldBeEmpty();
        outcome.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void the_conventional_route_really_does_bind_when_it_is_offered()
    {
        const string conventional =
            """
            using Bobcat;

            namespace Specs;

            public class BookingShipmentsFixture : Fixture
            {
                [Given("nothing")]
                public void GivenNothing() { }
            }
            """;

        var outcome = GeneratorHarness.Run(conventional,
            ("X.feature", "Feature: Booking Shipments\n\n  Scenario: S\n    Given nothing\n"));

        outcome.WithId("BOBCAT001").ShouldBeEmpty();
        outcome.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void a_title_with_spaces_is_not_told_to_write_an_illegal_identifier()
    {
        var advice = adviceFor("Wallet over HTTP", NoFixture);

        // "Wallet over HTTPFixture" does not compile, so it is not advice.
        advice.ShouldNotContain("Wallet over HTTPFixture");
    }

    [Fact]
    public void the_conventional_name_is_still_suggested_when_it_genuinely_works()
    {
        var advice = adviceFor("Booking Shipments", NoFixture);

        advice.ShouldContain("BookingShipmentsFixture");
    }

    [Fact]
    public void a_feature_bound_to_nothing_is_an_error_not_a_warning()
    {
        var outcome = GeneratorHarness.Run(NoFixture,
            ("X.feature", "Feature: BookingShipments\n\n  Scenario: S\n    Given nothing\n"));

        var diagnostic = outcome.WithId("BOBCAT001").ShouldHaveSingleItem();

        // Issue #273. A .feature in AdditionalFiles is a declaration of intent; no fixture for it
        // is a mistake. As a warning it scrolled past in a normal build, was invisible in CI, and
        // left a run reporting "Zero tests ran … total: 0" with exit code 0 — the one failure this
        // stack exists to prevent, arriving by a different route.
        diagnostic.Severity.ShouldBe(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void an_unmatched_step_was_already_an_error_which_is_why_this_one_was_the_odd_one_out()
    {
        const string fixture =
            """
            using Bobcat;

            namespace Specs;

            [FixtureTitle("BookingShipments")]
            public class BookingShipmentsFixture : Fixture
            {
                [Given("nothing")]
                public void GivenNothing() { }
            }
            """;

        var outcome = GeneratorHarness.Run(fixture,
            ("X.feature", "Feature: BookingShipments\n\n  Scenario: S\n    Given something else\n"));

        outcome.WithId("BOBCAT002").ShouldHaveSingleItem()
            .Severity.ShouldBe(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }
}
