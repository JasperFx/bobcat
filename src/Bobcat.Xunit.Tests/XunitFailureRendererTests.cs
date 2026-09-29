using Bobcat.Engine;
using Shouldly;
using Xunit;

namespace Bobcat.Xunit.Tests;

/// <summary>
/// xUnit's own failures, read as the expected/actual pair they carry — the adapter package's share of
/// the failure-rendering seam.
/// </summary>
/// <remarks>
/// Core cannot know what <c>Assert.Equal() Failure</c> means or how its message is laid out; it cannot
/// reference xUnit at all. A runner's adapter is where that runner's knowledge belongs.
/// </remarks>
public class XunitFailureRendererTests
{
    private static readonly XunitFailureRenderer renderer = new();

    [Fact]
    public void an_equality_failure_becomes_a_cell()
    {
        var failure = renderer.Render(new SpecFailureContext(
            "EqualException",
            "Assert.Equal() Failure: Values differ\nExpected: 7\nActual:   6"));

        failure.Kind.ShouldBe(SpecFailureKind.Assertion);
        failure.ShowStackTrace.ShouldBeFalse();

        var cell = failure.Cells.ShouldHaveSingleItem();
        cell.Name.ShouldBe("Assert.Equal()");
        cell.Expected.ShouldBe("7");
        cell.Actual.ShouldBe("6");
        cell.Status.ShouldBe(ResultStatus.failed);
    }

    [Fact]
    public void the_whole_assertion_family_is_claimed()
    {
        // Every xUnit assertion failure derives from XunitException and the concrete names are legion.
        renderer.Handles("EqualException").ShouldBeTrue();
        renderer.Handles("TrueException").ShouldBeTrue();
        renderer.Handles("ContainsException").ShouldBeTrue();
        renderer.Handles("XunitException").ShouldBeTrue();
        renderer.Handles("InvalidOperationException").ShouldBeFalse();
    }

    [Fact]
    public void a_message_with_no_expected_actual_pair_falls_back_to_the_message()
    {
        var failure = renderer.Render(new SpecFailureContext(
            "TrueException", "Assert.True() Failure\nExpected: True\n"));

        // "Actual:" is missing, so there is no pair to show and the message stands. Never worse than
        // before.
        failure.Cells.ShouldBeEmpty();
        failure.Message.ShouldContain("Assert.True() Failure");
    }

    [Fact]
    public void the_renderer_is_registered_by_the_package()
        // A module initializer, so a consumer referencing Bobcat.Xunit gets it with no wiring at all.
        => SpecFailureRenderers.Render(new SpecFailureContext(
                "EqualException", "Assert.Equal() Failure\nExpected: 1\nActual:   2"))
            .Cells.ShouldHaveSingleItem().Expected.ShouldBe("1");
}
