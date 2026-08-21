using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// Issue #122: a <c>[Table]</c> step with a Cucumber-expression capture in its text used to
/// receive <c>default</c> for the capture, with nothing but a CS8625 in the generated code.
/// </summary>
public class TableCaptureTests
{
    [Fact]
    public async Task a_table_step_receives_the_capture_from_its_text()
    {
        var results = await Specs.Run(Table_Capture_Feature.Define(), "A table step binds the capture in its text");

        results.Step("these steps ran in").StepStatus.ShouldBe(ResultStatus.success);
        results.Step("the log is").StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task captures_bind_to_the_parameters_no_column_names_regardless_of_order()
    {
        var results = await Specs.Run(Table_Capture_Feature.Define(), "Captures bind by role, not by position in the signature");

        results.Step("these 2 rows belong to").StepStatus.ShouldBe(ResultStatus.success);
        results.Step("the log is").StepStatus.ShouldBe(ResultStatus.success);
    }
}
