using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

public class GherkinParserTests
{
    [Fact]
    public async Task background_steps_run_first_and_docstring_is_captured()
    {
        var results = await Specs.Run(Parser_Features_Feature.Define(), "Background applies and docstring is captured");

        // Background "the base value is 10" ran first, so adding 5 gives 15.
        results.Step("adding 5 gives 15").StepStatus.ShouldBe(ResultStatus.success);
        // DocString was passed to the body parameter.
        results.Step("the body should contain").StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void scenario_outline_expands_to_one_scenario_per_example()
    {
        var feature = Parser_Features_Feature.Define();
        feature.Scenarios.Count(s => s.Title.StartsWith("Arithmetic over examples")).ShouldBe(3);
        feature.Scenarios.ShouldContain(s => s.Title == "Arithmetic over examples [Example 2]");
    }

    [Theory]
    [InlineData("Arithmetic over examples [Example 1]", "adding 1 gives 11")]
    [InlineData("Arithmetic over examples [Example 2]", "adding 2 gives 12")]
    [InlineData("Arithmetic over examples [Example 3]", "adding 5 gives 15")]
    public async Task outline_examples_substitute_placeholders_and_pass(string scenario, string stepText)
    {
        var results = await Specs.Run(Parser_Features_Feature.Define(), scenario);
        results.Step(stepText).StepStatus.ShouldBe(ResultStatus.success);
    }
}
