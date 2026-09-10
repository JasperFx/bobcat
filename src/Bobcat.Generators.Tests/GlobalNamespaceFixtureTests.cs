using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// Issue #269: a fixture declared with no namespace made the generator write Roslyn's
/// <em>display string</em> for the global namespace into the generated file, verbatim.
/// </summary>
/// <remarks>
/// <code>
/// // Widgets_Feature.g.cs
/// namespace &lt;global namespace&gt;;
/// </code>
/// followed by 14 compile errors (CS1001, CS1514, CS0116, …) in a file the author did not write
/// and cannot edit. A file-scoped namespace is a thing people add on the second pass, so the
/// shape that trips it — one class, one feature, straight off the Getting Started page — is
/// precisely the first thing a new user builds.
///
/// These tests assert on the COMPILED result rather than on the emitted text, because the text
/// was never the complaint: a generator that emits something unparseable produces no diagnostic
/// of its own, and the only honest question is whether the project builds.
/// </remarks>
public class GlobalNamespaceFixtureTests
{
    private const string Feature =
        """
        Feature: Widgets

          Scenario: Counting them
            Given there are 3 widgets
            Then the count is 3
        """;

    private const string GlobalNamespaceFixture =
        """
        using Bobcat;

        public class Widgets : Fixture
        {
            private int _count;

            [Given("there are {int} widgets")]
            public void GivenWidgets(int count) => _count = count;

            [Then("the count is {int}")]
            public void ThenCount(int expected) { }
        }
        """;

    [Fact]
    public void a_fixture_with_no_namespace_generates_code_that_compiles()
    {
        var outcome = GeneratorHarness.Run(GlobalNamespaceFixture, ("Widgets.feature", Feature));

        outcome.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void the_global_namespace_display_string_never_reaches_the_generated_source()
    {
        var outcome = GeneratorHarness.Run(GlobalNamespaceFixture, ("Widgets.feature", Feature));

        // The literal that used to be emitted. Naming it keeps the test readable as the bug it pins.
        outcome.GeneratedSource("Widgets").ShouldNotContain("<global namespace>");
    }

    [Fact]
    public void no_namespace_declaration_is_emitted_at_all_rather_than_an_empty_one()
    {
        var outcome = GeneratorHarness.Run(GlobalNamespaceFixture, ("Widgets.feature", Feature));

        // `namespace ;` would also be 14 compile errors — the guard has to skip the line, not blank it.
        outcome.GeneratedSource("Widgets").ShouldNotContain("namespace ");
    }

    [Fact]
    public void a_fixture_in_a_namespace_is_unaffected()
    {
        const string namespaced =
            """
            using Bobcat;

            namespace Specs.Widgets;

            public class Widgets : Fixture
            {
                [Given("there are {int} widgets")]
                public void GivenWidgets(int count) { }

                [Then("the count is {int}")]
                public void ThenCount(int expected) { }
            }
            """;

        var outcome = GeneratorHarness.Run(namespaced, ("Widgets.feature", Feature));

        outcome.CompilationErrors.ShouldBeEmpty();
        outcome.GeneratedSource("Widgets").ShouldContain("namespace Specs.Widgets;");
    }
}
