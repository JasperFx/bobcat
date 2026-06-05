using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

public class ReturnAndOutVerificationTests
{
    [Fact]
    public async Task return_value_match_passes_with_structured_cell()
    {
        var results = await Specs.Run(Return_Value_Feature.Define(), "Addition passes");
        var step = results.Step("2 plus 3 should be 5");

        step.StepStatus.ShouldBe(ResultStatus.success);
        var cell = step.Cells.Single();
        cell.Name.ShouldBe("result");
        cell.Expected.ShouldBe("5");
        cell.Actual.ShouldBe("5");
    }

    [Fact]
    public async Task return_value_mismatch_fails_with_expected_and_actual()
    {
        var results = await Specs.Run(Return_Value_Feature.Define(), "Addition fails");
        var step = results.Step("2 plus 3 should be 6");

        step.StepStatus.ShouldBe(ResultStatus.failed);
        var cell = step.Cells.Single();
        cell.Status.ShouldBe(ResultStatus.failed);
        cell.Expected.ShouldBe("6");
        cell.Actual.ShouldBe("5");
        cell.DisplayText.ShouldBe("expected '6', got '5'");
    }

    [Fact]
    public async Task approx_tolerance_flows_into_comparison()
    {
        var results = await Specs.Run(Return_Value_Feature.Define(), "Approximate average passes within tolerance");
        var step = results.Step("the average of 1 and 2 is 1.55");

        step.StepStatus.ShouldBe(ResultStatus.success);
        step.Cells.Single().Note.ShouldBe("±0.1");
    }

    [Fact]
    public async Task string_return_value_passes()
    {
        var results = await Specs.Run(Return_Value_Feature.Define(), "String return passes");
        results.Step("greeting").StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task string_return_value_fails()
    {
        var results = await Specs.Run(Return_Value_Feature.Define(), "String return fails");
        var cell = results.Step("greeting").Cells.Single();
        cell.Status.ShouldBe(ResultStatus.failed);
        cell.Actual.ShouldBe("Hello World");
        cell.Expected.ShouldBe("Goodbye World");
    }

    [Fact]
    public async Task out_params_both_correct_passes()
    {
        var results = await Specs.Run(Out_Params_Feature.Define(), "Both outputs correct");
        var step = results.Step("dividing 17 by 5");

        step.StepStatus.ShouldBe(ResultStatus.success);
        step.Cells.Count.ShouldBe(2);
        step.Cells.ShouldContain(c => c.Name == "quotient" && c.Status == ResultStatus.success);
        step.Cells.ShouldContain(c => c.Name == "remainder" && c.Status == ResultStatus.success);
    }

    [Fact]
    public async Task out_params_one_wrong_fails_and_names_the_cell()
    {
        var results = await Specs.Run(Out_Params_Feature.Define(), "One output wrong");
        var step = results.Step("dividing 17 by 5");

        step.StepStatus.ShouldBe(ResultStatus.failed);
        var remainder = step.Cells.Single(c => c.Name == "remainder");
        remainder.Status.ShouldBe(ResultStatus.failed);
        remainder.Expected.ShouldBe("0");
        remainder.Actual.ShouldBe("2");
        step.Cells.Single(c => c.Name == "quotient").Status.ShouldBe(ResultStatus.success);
    }
}
