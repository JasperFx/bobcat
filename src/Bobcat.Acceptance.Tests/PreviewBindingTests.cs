using Bobcat.Rendering;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// Issue #208: the generator writes down the match it acted on — which method a step bound to,
/// via which expression, and where every parameter's value comes from — and the preview model
/// reads it back off the plan without executing anything. These run against the REAL generated
/// features, so the metadata can never drift from what the emit actually does.
/// </summary>
public class PreviewBindingTests
{
    private static PreviewRender preview(FeatureDefinition feature, string scenarioTitle)
    {
        var scenario = feature.Scenarios.First(s => s.Title == scenarioTitle);
        var render = PreviewRender.FromScenario(feature, scenario);
        render.Error.ShouldBeNull();
        return render;
    }

    [Fact]
    public void a_capture_binds_positionally_and_says_so()
    {
        var render = preview(Out_Params_Feature.Define(), "Both outputs correct");

        var binding = render.Steps.Single().Binding.ShouldNotBeNull();
        binding.DeclaringTypeName.ShouldBe("OutParamsFixture");
        binding.Method.ShouldBe("DivMod");
        binding.Expression.ShouldBe("dividing {int} by {int} gives {int} remainder {int}");

        // Inputs are captures, out parameters are the expected values compared afterwards.
        binding.Arguments.Select(a => (a.Name, a.Value, a.Source)).ShouldBe(
        [
            ("dividend", "17", StepArgumentSource.Capture),
            ("divisor", "5", StepArgumentSource.Capture),
            ("quotient", "3", StepArgumentSource.Expected),
            ("remainder", "2", StepArgumentSource.Expected)
        ]);
    }

    [Fact]
    public void an_injected_service_parameter_is_reported_as_injected_not_captured()
    {
        var render = preview(Di_Scoping_Feature.Define(), "One scoped instance is shared by every step in a scenario");

        var binding = render.Steps.First(s => s.StepText == "the scoped session is captured").Binding.ShouldNotBeNull();
        binding.Method.ShouldBe("Capture");

        var argument = binding.Arguments.Single();
        argument.Name.ShouldBe("session");
        argument.Source.ShouldBe(StepArgumentSource.Service);
        argument.Value.ShouldBe("Bobcat.Acceptance.Tests.ISessionMarker");
    }

    [Fact]
    public void a_table_step_maps_columns_to_parameters_by_header()
    {
        var render = preview(Di_Scoping_Feature.Define(), "ScopePerRow isolates each table row");

        var rowStep = render.Steps.First(s => s.StepText.Contains("(row 1)"));
        var binding = rowStep.Binding.ShouldNotBeNull();
        binding.Method.ShouldBe("CaptureRow");
        binding.Arguments.Select(a => (a.Name, a.Source)).ShouldBe(
        [
            ("label", StepArgumentSource.TableColumn),
            ("session", StepArgumentSource.Service)
        ]);
    }

    [Fact]
    public void a_decision_table_marks_output_columns_as_expected()
    {
        var render = preview(Decision_Table_Feature.Define(), "Out-param columns all pass");

        var binding = render.Steps.Single().Binding.ShouldNotBeNull();
        binding.Method.ShouldBe("DivModTable");
        binding.Arguments.Select(a => (a.Name, a.Value, a.Source)).ShouldBe(
        [
            ("dividend", "dividend", StepArgumentSource.TableColumn),
            ("divisor", "divisor", StepArgumentSource.TableColumn),
            ("quotient", "quotient", StepArgumentSource.Expected),
            ("remainder", "remainder", StepArgumentSource.Expected)
        ]);
    }

    [Fact]
    public void a_compared_return_value_appears_as_a_trailing_expected_argument()
    {
        var render = preview(Decision_Table_Feature.Define(), "Return-value columns all pass");

        var binding = render.Steps.Single().Binding.ShouldNotBeNull();
        binding.Method.ShouldBe("LineTotal");
        binding.Arguments.Last().Name.ShouldBe("LineTotal");
        binding.Arguments.Last().Source.ShouldBe(StepArgumentSource.Expected);
    }

    [Fact]
    public void a_table_grammar_binds_to_the_grammar_class_and_its_row_method()
    {
        var render = preview(Table_Grammar_Feature.Define(), "Decision table");

        var binding = render.Steps.Single().Binding.ShouldNotBeNull();
        binding.DeclaringTypeName.ShouldBe("DivisionGrammar");
        binding.Method.ShouldBe("Row");
        binding.Expression.ShouldBe("dividing gives");
        binding.Arguments.Select(a => (a.Name, a.Source)).ShouldBe(
        [
            ("dividend", StepArgumentSource.TableColumn),
            ("divisor", StepArgumentSource.TableColumn),
            ("quotient", StepArgumentSource.Expected)
        ]);
    }

    [Fact]
    public void every_generated_scenario_previews_without_executing()
    {
        // Table Grammar includes a grammar whose Before deliberately throws when run, and no
        // resource or scenario scope exists here — so composing every plan cleanly is itself
        // evidence nothing executed. (The unit-level proof lives in PreviewRenderTests.)
        var feature = Table_Grammar_Feature.Define();

        foreach (var scenario in feature.Scenarios)
        {
            var render = PreviewRender.FromScenario(feature, scenario);
            render.Error.ShouldBeNull();
            render.Steps.ShouldNotBeEmpty();
        }
    }
}
