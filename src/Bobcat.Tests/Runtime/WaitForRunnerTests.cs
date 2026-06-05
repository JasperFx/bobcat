using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

public class WaitForRunnerTests
{
    private static CellResult[] One(ResultStatus status, string expected, string actual)
        => new[] { new CellResult("v", status) { Expected = expected, Actual = actual } };

    [Fact]
    public async Task converges_and_marks_success_with_note()
    {
        var step = new StepResult("s", 0);
        var attempts = 0;

        await WaitForRunner.Poll(step, 2000, 5, _ =>
        {
            attempts++;
            var ok = attempts >= 3;
            return Task.FromResult(new WaitAttempt(ok, One(ok ? ResultStatus.success : ResultStatus.failed, "0", ok ? "0" : "5")));
        }, CancellationToken.None);

        step.StepStatus.ShouldBe(ResultStatus.success);
        step.Cells.Single().Note!.ShouldContain("converged after");
        attempts.ShouldBe(3);
    }

    [Fact]
    public async Task times_out_and_reports_last_actual()
    {
        var step = new StepResult("s", 0);

        await WaitForRunner.Poll(step, 40, 5, _ =>
            Task.FromResult(new WaitAttempt(false, One(ResultStatus.failed, "0", "9"))),
            CancellationToken.None);

        step.StepStatus.ShouldBe(ResultStatus.failed);
        var cell = step.Cells.Single();
        cell.Actual.ShouldBe("9");
        cell.Note!.ShouldContain("timed out after 40ms @5ms");
    }

    [Fact]
    public async Task swallows_exceptions_during_window_and_surfaces_last_on_timeout()
    {
        var step = new StepResult("s", 0);

        await WaitForRunner.Poll(step, 40, 5,
            attempt: _ => throw new InvalidOperationException("boom"),
            ct: CancellationToken.None);

        step.StepStatus.ShouldBe(ResultStatus.failed);
        step.Cells.Single().Note!.ShouldContain("last error: boom");
    }
}
