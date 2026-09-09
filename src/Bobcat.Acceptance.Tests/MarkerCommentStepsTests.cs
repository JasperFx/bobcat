using Bobcat;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// Issue #110, end to end and in a real compilation: the comments below are erased by the
/// compiler, and they still arrive at runtime as this scenario's declared steps.
/// </summary>
/// <remarks>
/// The generator runs over this very assembly, so nothing here is a stand-in — if the module
/// initializer were not emitted, or the comments not read, these assertions fail. That is the only
/// check that covers the whole path; a parser test cannot tell you the steps ever left the build.
/// </remarks>
[BobcatFeature("Marker comments")]
public class MarkerCommentStepsTests
{
    [Fact]
    public void a_test_declares_its_steps_in_comments()
    {
        // Given an ordinary test method
        var uid = "Marker comments/a test declares its steps in comments";

        // this line explains something and is not a step
        var declared = DeclaredSteps.For(uid);

        // When the declared steps are read back
        var rendered = declared.Select(x => x.ToString()).ToArray();

        // Then they are the comments, in source order
        rendered.ShouldBe(
        [
            "Given an ordinary test method",
            "When the declared steps are read back",
            "Then they are the comments, in source order"
        ]);
    }

    [Fact]
    public void every_declared_step_carries_its_source_line()
    {
        // Given a declared scenario
        var declared = DeclaredSteps.For("Marker comments/a test declares its steps in comments");

        // Then each step knows the line it came from
        declared.Select(x => x.Line).ShouldBeInOrder();
        declared.First().Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void a_test_with_no_marker_comments_declares_nothing()
    {
        DeclaredSteps.For("Marker comments/a test with no marker comments declares nothing")
            .ShouldBeEmpty();
    }
}
