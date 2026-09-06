using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// Issue #212 phase 2 end to end, through the real generator: attribute literals construct the
/// module, scenario-scope parameters resolve like step parameters (forcing the lazy in-scope
/// construction), trailing optionals may be omitted, [IncludeGrammars] is inherited from base
/// classes, and the most-derived declaration re-parameterizes a base-declared module.
/// </summary>
public class ParameterizedModulesTests
{
    [Fact]
    public async Task attribute_literals_construct_the_module_and_trailing_optionals_are_omitted()
    {
        var results = await Specs.Run(Parameterized_Composed_Feature.Define(),
            "A module is constructed with the attribute literals");
        results.Step("the prefixed echo of \"x\" should be \"pre-x!\"").StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task uncovered_constructor_parameters_resolve_from_the_scenario_scope()
    {
        var results = await Specs.Run(Parameterized_Composed_Feature.Define(),
            "A module resolves its remaining constructor parameters from the scenario");
        results.Step("the bound label should be \"wallet\"").StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task include_grammars_is_discovered_on_base_classes()
    {
        var results = await Specs.Run(Derived_Composed_Feature.Define(),
            "A base-declared module binds through the derived fixture");
        results.Step("the inherited flag should be \"on\"").StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task the_most_derived_declaration_reparameterizes_a_base_declared_module()
    {
        var results = await Specs.Run(Derived_Composed_Feature.Define(),
            "The most-derived declaration re-parameterizes the module");
        results.Step("the prefixed echo of \"x\" should be \"derived-x!\"").StepStatus.ShouldBe(ResultStatus.success);
    }
}
