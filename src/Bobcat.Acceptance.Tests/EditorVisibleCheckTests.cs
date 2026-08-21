using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// A <c>[Check]</c> stacked with a <c>[Then]</c> of the same expression must still run as a
/// check in either attribute order. Before the generator made <c>[Check]</c> sticky, the last
/// attribute won, so a "check first" stack quietly became a plain <c>[Then]</c> that called the
/// method and discarded its bool — the negative check below reported success. That is the
/// regression these tests exist to catch.
/// </summary>
public class EditorVisibleCheckTests
{
    [Fact]
    public async Task check_written_after_then_is_still_a_check()
    {
        var results = await Specs.Run(Editor_Visible_Check_Feature.Define(), "Check written after Then");
        results.Step("positive with then first").StepStatus.ShouldBe(ResultStatus.success);
        results.Step("negative with then first").StepStatus.ShouldBe(ResultStatus.failed);
    }

    [Fact]
    public async Task check_written_before_then_is_still_a_check()
    {
        var results = await Specs.Run(Editor_Visible_Check_Feature.Define(), "Check written before Then");
        results.Step("positive with check first").StepStatus.ShouldBe(ResultStatus.success);
        results.Step("negative with check first").StepStatus.ShouldBe(ResultStatus.failed);
    }
}
