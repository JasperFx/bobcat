using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

public class IncludeGrammarsTests
{
    [Fact]
    public async Task module_steps_run_alongside_fixture_steps_sharing_module_state()
    {
        var results = await Specs.Run(Composed_Feature.Define(), "Module steps work alongside the fixture's own");

        // Module's return-value verification: 5 + 1 + 1 = 7
        results.Step("the counter should be 7").StepStatus.ShouldBe(ResultStatus.success);
        // Fixture's own check still works
        results.Step("the fixture's own check passes").StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task module_instance_is_fresh_per_scenario()
    {
        var results = await Specs.Run(Composed_Feature.Define(), "Module instance is fresh per scenario");
        // Would be 8 if the module instance leaked from the previous scenario.
        results.Step("the counter should be 1").StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task fixture_derived_module_receives_context()
    {
        var results = await Specs.Run(Composed_Feature.Define(), "Fixture-derived module receives context");
        results.Step("the module received a context").StepStatus.ShouldBe(ResultStatus.success);
    }
}
