using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

public class WaitForTests
{
    [Fact]
    public async Task return_value_converges_and_marks_success()
    {
        var results = await Specs.Run(Wait_For_Feature.Define(), "Return value converges");
        var step = results.Step("the outstanding count becomes 0");

        step.StepStatus.ShouldBe(ResultStatus.success);
        var cell = step.Cells.Single();
        cell.Status.ShouldBe(ResultStatus.success);
        cell.Note.ShouldNotBeNull();
        cell.Note!.ShouldContain("converged after");
    }

    [Fact]
    public async Task timeout_fails_reporting_last_actual_value()
    {
        var results = await Specs.Run(Wait_For_Feature.Define(), "Return value times out");
        var step = results.Step("the never-ready count becomes 0");

        step.StepStatus.ShouldBe(ResultStatus.failed);
        var cell = step.Cells.Single();
        cell.Status.ShouldBe(ResultStatus.failed);
        cell.Expected.ShouldBe("0");
        cell.Actual.ShouldBe("9"); // last actual value is reported
        cell.Note!.ShouldContain("timed out after 60ms @10ms");
    }

    [Fact]
    public async Task check_bool_converges()
    {
        var results = await Specs.Run(Wait_For_Feature.Define(), "Check converges");
        var step = results.Step("the system is eventually ready");

        step.StepStatus.ShouldBe(ResultStatus.success);
        step.Cells.Single().Note!.ShouldContain("converged after");
    }

    [Fact]
    public async Task void_action_retries_until_no_throw()
    {
        var results = await Specs.Run(Wait_For_Feature.Define(), "Action eventually succeeds");
        var step = results.Step("the queue eventually drains");

        step.StepStatus.ShouldBe(ResultStatus.success);
    }
}
