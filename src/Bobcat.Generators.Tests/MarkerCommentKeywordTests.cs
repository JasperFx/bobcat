using Microsoft.CodeAnalysis;
using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// <c>And</c> and <c>But</c> continue a narrative and cannot start one — and when a comment is read as
/// prose because of that rule, the build says so.
/// </summary>
/// <remarks>
/// English sentences begin "And …" and "But …" constantly, and a comment in a test body is
/// overwhelmingly not a step. The author of this rule wrote <c>// And a false one fails its step</c> as a
/// note to a reader and watched it become a step that wrapped the two real steps under a narrative row
/// nobody wrote. A keyword that can only continue something must not be able to start it.
/// </remarks>
public class MarkerCommentKeywordTests
{
    private static string Source(string body) =>
        $$"""
        using Bobcat;
        using Xunit;

        namespace Specs;

        [BobcatFeature("Rebuilding")]
        public class rebuilding_specs
        {
            [Fact]
            public void a_test()
            {
        {{body}}
                var x = 1;
            }
        }
        """;

    private static string Generated(string body)
        => GeneratorHarness.Run(Source(body)).GeneratedSource("BobcatDeclaredSteps");

    private static IReadOnlyList<Diagnostic> Diagnostics(string body)
        => GeneratorHarness.Run(Source(body)).Diagnostics;

    [Fact]
    public void an_and_comment_with_no_narrative_open_declares_nothing()
    {
        var outcome = GeneratorHarness.Run(
            Source("        // And this is a note to whoever maintains the test"));

        // No registration file at all. A test with no marker comments is not a specification, and the
        // generator declines to emit an initializer rather than announcing a scenario with nothing to
        // say — which is what makes this assertion "no file", not "an empty file".
        outcome.Result.Results
            .SelectMany(r => r.GeneratedSources)
            .ShouldNotContain(s => s.HintName.Contains("BobcatDeclaredSteps"));
    }

    [Fact]
    public void an_and_comment_with_a_narrative_open_is_a_step()
    {
        var code = Generated(
            """
                    // Given an open narrative
                    // And a step continuing it
            """);

        code.ShouldContain("Rebuilding/a test");
        code.ShouldContain("\"And\", \"a step continuing it\"");
    }

    [Fact]
    public void a_bullet_opens_the_narrative_too()
    {
        var code = Generated(
            """
                    // * a keywordless step
                    // And a step continuing it
            """);

        code.ShouldContain("\"And\", \"a step continuing it\"");
    }

    [Fact]
    public void the_comment_read_as_prose_is_reported()
    {
        var diagnostics = Diagnostics("        // But this is only a note");

        // Info, not a warning: the overwhelmingly common case is that the comment really is prose and
        // everything is fine, and a warning there would train people to ignore it. It exists for the
        // one author who meant a step and cannot see why it is missing.
        var reported = diagnostics.ShouldHaveSingleItem();
        reported.Id.ShouldBe("BOBCAT029");
        reported.Severity.ShouldBe(DiagnosticSeverity.Info);
        reported.GetMessage().ShouldContain("cannot start one");
        reported.GetMessage().ShouldContain("* this is only a note");
    }

    [Fact]
    public void a_narrated_and_comment_is_not_reported()
        => Diagnostics(
            """
                    // Given an open narrative
                    // And a step continuing it
            """).ShouldBeEmpty();
}
