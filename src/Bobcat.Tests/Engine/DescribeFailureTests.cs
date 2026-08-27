using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Tests.Engine;

/// <summary>
/// Issue #166's side observation, confirmed real: a failing scenario reached the console with
/// <c>errorMessage: null</c> because the publisher only ever read <c>Exception?.Message</c>,
/// and a pure assertion failure — a [Check] returning false, a table comparison — carries no
/// exception. <see cref="StepResult.DescribeFailure"/> is the one account of "why is this red"
/// whatever form the failure took.
/// </summary>
public class DescribeFailureTests
{
    private static StepResult step(ResultStatus status = ResultStatus.ok)
    {
        var result = new StepResult("s1", 0, StepKind.Then) { StepText = "the total is 7" };
        if (status == ResultStatus.failed) result.MarkFailed();
        return result;
    }

    [Fact]
    public void a_green_step_describes_nothing()
    {
        var result = step();
        result.MarkSuccess();
        result.DescribeFailure().ShouldBeNull();
    }

    [Fact]
    public void an_exception_wins_over_everything()
    {
        var result = step();
        result.MarkErrored(new InvalidOperationException("boom"), end: 5);
        result.DescribeFailure().ShouldBe("boom");
    }

    [Fact]
    public void a_failed_check_with_no_cells_names_the_step()
    {
        step(ResultStatus.failed).DescribeFailure().ShouldBe("'the total is 7' returned false");
    }

    [Fact]
    public void failed_cells_describe_the_comparison_even_when_the_step_status_stayed_ok()
    {
        // The executor marks a step successful whenever it completed without throwing;
        // comparison verdicts live on the cells.
        var result = step();
        result.MarkSuccess();
        result.MarkCells(
            new CellResult("total", ResultStatus.failed) { Expected = "7", Actual = "8" },
            new CellResult("name", ResultStatus.ok) { Actual = "fine" });

        result.DescribeFailure().ShouldBe("total: expected 7, got 8");
    }

    [Fact]
    public void a_cell_without_structured_comparison_falls_back_to_its_display_text()
    {
        var result = step();
        result.MarkSuccess();
        result.MarkCells(new CellResult("missing-row", ResultStatus.missing, "Missing row: Id=5"));

        result.DescribeFailure().ShouldBe("missing-row: Missing row: Id=5");
    }

    [Fact]
    public void the_scenario_level_account_is_the_first_failing_steps()
    {
        var results = new ExecutionResults("spec", DateTimeOffset.UtcNow);
        results.StartStep("s1", 0).MarkSuccess();
        var failing = results.StartStep("s2", 1);
        failing.StepText = "the balance is 100";
        failing.MarkFailed();
        results.StartStep("s3", 2).MarkErrored(new Exception("later"), 3);

        results.DescribeFailure().ShouldBe("'the balance is 100' returned false");
    }

    [Fact]
    public void a_green_scenario_describes_nothing()
    {
        var results = new ExecutionResults("spec", DateTimeOffset.UtcNow);
        results.StartStep("s1", 0).MarkSuccess();
        results.DescribeFailure().ShouldBeNull();
    }
}
