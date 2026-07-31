using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

public class WaitForRunnerTests
{
    private static CellResult[] one(ResultStatus status, string expected, string actual)
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
            return Task.FromResult(new WaitAttempt(ok, one(ok ? ResultStatus.success : ResultStatus.failed, "0", ok ? "0" : "5")));
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
            Task.FromResult(new WaitAttempt(false, one(ResultStatus.failed, "0", "9"))),
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

    [Fact]
    public async Task reports_interim_progress_with_last_value_before_converging()
    {
        var step = new StepResult("s", 0);
        var updates = new List<StepUpdate>();
        var context = new SpecExecutionContext("spec") { ProgressSink = updates.Add };

        var attempts = 0;

        await WaitForRunner.Poll(step, 2000, 1, _ =>
        {
            attempts++;
            var ok = attempts >= 3;
            return Task.FromResult(new WaitAttempt(ok, one(ok ? ResultStatus.success : ResultStatus.failed, "0", ok ? "0" : "5")));
        }, CancellationToken.None, context);

        step.StepStatus.ShouldBe(ResultStatus.success);

        // Two non-converged attempts before the third converges → at least one live update,
        // each carrying the latest "last value" and the per-attempt cells.
        updates.ShouldNotBeEmpty();
        updates.ShouldAllBe(u => u.Message!.Contains("last value 5"));
        updates[0].Cells.Single().Actual.ShouldBe("5");
    }

    [Fact]
    public async Task reports_last_error_while_polling_through_a_swallowed_exception()
    {
        var step = new StepResult("s", 0);
        var updates = new List<StepUpdate>();
        var context = new SpecExecutionContext("spec") { ProgressSink = updates.Add };

        var attempts = 0;

        await WaitForRunner.Poll(step, 2000, 1, _ =>
        {
            attempts++;
            if (attempts < 3) throw new InvalidOperationException("not ready");
            return Task.FromResult(new WaitAttempt(true, one(ResultStatus.success, "0", "0")));
        }, CancellationToken.None, context);

        step.StepStatus.ShouldBe(ResultStatus.success);
        updates.ShouldContain(u => u.Message!.Contains("last error: not ready"));
    }
}
