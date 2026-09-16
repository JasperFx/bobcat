using Bobcat.Generators;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// Issue #110: steps declared by marker comments in an ordinary xUnit test.
/// </summary>
/// <remarks>
/// The subject is deliberately a test that looks like one already in a real suite — a couple of
/// awaits, a local, a comment that is just a comment — because the point of this authoring style
/// is that an existing suite can adopt it by adding comments, and the risk is reading too much.
/// </remarks>
public class MarkerCommentSpecTests
{
    private const string Source =
        """
        using Xunit;

        [BobcatFeature("Async daemon")]
        public class when_the_daemon_catches_up
        {
            [Fact]
            public async Task a_proposed_appointment_is_confirmed()
            {
                // Given a proposed appointment
                var id = Guid.NewGuid();
                await AppendAsync(id, new HomeCheckAppointmentProposed());

                // the daemon polls on its own schedule here, which is why the wait below exists
                await WaitForNonStaleAsync();

                // When the owner confirms
                var response = await PostAsync(new ConfirmAppointment(id));

                // Then it is confirmed
                response.Status.ShouldBe("Confirmed");
                // And the queue no longer awaits action
                queue.AwaitingAction.ShouldBeFalse();
            }

            [Fact]
            public void an_ordinary_test_with_no_markers()
            {
                // just a comment
                Assert.True(true);
            }

            private async Task helper() { /* Given not a step: not a test method */ }
        }
        """;

    private static MethodDeclarationSyntax Method(string name)
        => CSharpSyntaxTree.ParseText(Source).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == name);

    [Fact]
    public void reads_the_steps_in_source_order()
    {
        var steps = MarkerCommentSpecs.StepsIn(Method("a_proposed_appointment_is_confirmed")).ToList();

        steps.Select(x => $"{x.Keyword} {x.Text}").ShouldBe(
        [
            "Given a proposed appointment",
            "When the owner confirms",
            "Then it is confirmed",
            "And the queue no longer awaits action"
        ]);
    }

    [Fact]
    public void an_ordinary_comment_is_not_a_step()
    {
        // The one that decides whether this is usable on an existing suite: a real test is full of
        // explanatory comments, and promoting them to steps would make the rendering worse than
        // having none.
        MarkerCommentSpecs.StepsIn(Method("a_proposed_appointment_is_confirmed"))
            .ShouldNotContain(x => x.Text.Contains("polls on its own schedule"));

        MarkerCommentSpecs.StepsIn(Method("an_ordinary_test_with_no_markers")).ShouldBeEmpty();
    }

    [Fact]
    public void every_step_carries_the_line_it_came_from()
    {
        // Carried now because mapping a failure back to the step it fell inside needs it, and the
        // information only exists here.
        var steps = MarkerCommentSpecs.StepsIn(Method("a_proposed_appointment_is_confirmed")).ToList();

        steps.Select(x => x.Line).ShouldBe(steps.Select(x => x.Line).OrderBy(x => x));
        steps.First().Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void only_a_runner_visible_method_is_a_scenario()
    {
        MarkerCommentSpecs.IsTestMethod(Method("a_proposed_appointment_is_confirmed")).ShouldBeTrue();
        MarkerCommentSpecs.IsTestMethod(Method("helper")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("// Given a proposed appointment", "Given", "a proposed appointment")]
    [InlineData("//When the owner confirms", "When", "the owner confirms")]
    [InlineData("   //   Then   it is confirmed  ", "Then", "it is confirmed")]
    [InlineData("// And the queue is empty", "And", "the queue is empty")]
    [InlineData("// But nothing was published", "But", "nothing was published")]
    public void parses_a_marker(string comment, string keyword, string text)
    {
        var step = MarkerCommentSpecs.Parse(comment).ShouldNotBeNull();
        step.Keyword.ShouldBe(keyword);
        step.Text.ShouldBe(text);
    }

    [Theory]
    [InlineData("// just a comment")]
    [InlineData("// Givenchy is a fashion house")]
    [InlineData("// TODO: Given this a better name")]
    [InlineData("// Given")]
    [InlineData("//")]
    public void is_not_a_marker(string comment)
        => MarkerCommentSpecs.Parse(comment).ShouldBeNull();
}

/// <summary>
/// Issue #324: a marker comment may carry no keyword at all. Bobcat's rendering does not depend on
/// Given/When/Then and should not — the inspiration is Gauge, whose specs are bulleted sentences.
/// </summary>
public class BulletedStepTests
{
    [Theory]
    [InlineData("// * the household is already set up", "the household is already set up")]
    [InlineData("//* no space after the bullet", "no space after the bullet")]
    [InlineData("    //   *   padded all round   ", "padded all round")]
    public void a_bulleted_comment_is_a_step_with_no_keyword(string comment, string expected)
    {
        var step = MarkerCommentSpecs.Parse(comment).ShouldNotBeNull();

        step.Keyword.ShouldBe("");
        step.Text.ShouldBe(expected);
    }

    [Fact]
    public void an_ordinary_comment_is_still_not_a_step()
    {
        // The bullet has to be marked precisely because most comments in a test body are not steps.
        // Treating every comment as one would bury the real steps in noise.
        MarkerCommentSpecs.Parse("// just explaining the next line").ShouldBeNull();
        MarkerCommentSpecs.Parse("// TODO: come back to this").ShouldBeNull();
    }

    [Fact]
    public void a_bullet_with_nothing_after_it_is_not_a_step()
    {
        MarkerCommentSpecs.Parse("// *").ShouldBeNull();
        MarkerCommentSpecs.Parse("// *   ").ShouldBeNull();
    }

    [Fact]
    public void keywords_still_work_exactly_as_before()
    {
        var step = MarkerCommentSpecs.Parse("// Given a proposed appointment").ShouldNotBeNull();
        step.Keyword.ShouldBe("Given");
        step.Text.ShouldBe("a proposed appointment");
    }

    [Fact]
    public void a_keyword_free_step_renders_as_its_sentence_alone()
    {
        // The runtime already had this: DeclaredStep.ToString() omits an empty keyword rather than
        // emitting a leading space. What was missing was any way to WRITE one.
        new DeclaredStep("", "the household is already set up", 12).ToString()
            .ShouldBe("the household is already set up");
        new DeclaredStep("Given", "a proposed appointment", 12).ToString()
            .ShouldBe("Given a proposed appointment");
    }
}
