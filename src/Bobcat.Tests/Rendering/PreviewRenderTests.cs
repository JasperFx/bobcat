using Bobcat.Engine;
using Bobcat.Rendering;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Rendering;

/// <summary>
/// Issue #208: the preview model is pure plan composition — steps are listed, never executed,
/// and a definition with no generated metadata degrades to "no binding" rather than an error.
/// </summary>
public class PreviewRenderTests
{
    public class PlainFixture : Fixture;

    public class ThrowingFixture : Fixture
    {
        public ThrowingFixture() => throw new InvalidOperationException("needs a live host");
    }

    private static readonly List<string> executed = new();

    private static ScenarioDefinition scenario(string title, params string[] tags)
        => new(title, tags, (_, plan) =>
            plan.Add(new DelegateExecutionStep("step", StepKind.When, $"{title} acts", (_, result, _) =>
            {
                executed.Add(title);
                result.MarkSuccess();
                return Task.CompletedTask;
            })));

    [Fact]
    public void lists_the_planned_steps_without_executing_any()
    {
        executed.Clear();
        var feature = new FeatureDefinition("Orders", typeof(PlainFixture), [scenario("places", "slow")]);

        var render = PreviewRender.FromScenario(feature, feature.Scenarios[0]);

        render.Title.ShouldBe("places");
        render.FeatureTitle.ShouldBe("Orders");
        render.Tags.ShouldBe(["slow"]);
        render.Error.ShouldBeNull();
        render.Steps.Single().StepText.ShouldBe("places acts");
        render.Steps.Single().Kind.ShouldBe(StepKind.When);

        executed.ShouldBeEmpty("preview must never execute a step");
    }

    [Fact]
    public void a_step_without_generated_metadata_has_a_null_binding_not_an_error()
    {
        var feature = new FeatureDefinition("Orders", typeof(PlainFixture), [scenario("places")]);

        var render = PreviewRender.FromScenario(feature, feature.Scenarios[0]);

        render.Steps.Single().Binding.ShouldBeNull();
    }

    [Fact]
    public void a_fixture_that_cannot_even_construct_is_reported_not_thrown()
    {
        var feature = new FeatureDefinition("Orders", typeof(ThrowingFixture), [scenario("places")]);

        var render = PreviewRender.FromScenario(feature, feature.Scenarios[0]);

        render.Error.ShouldNotBeNull();
        render.Error.ShouldContain("needs a live host");
        render.Steps.ShouldBeEmpty();
    }

    [Fact]
    public void binding_type_names_render_without_namespace_qualification()
    {
        new StepBinding("global::My.App.OrderFixture", "Place", "an order is placed", [])
            .DeclaringTypeName.ShouldBe("OrderFixture");
        new StepBinding("OrderFixture", "Place", "an order is placed", [])
            .DeclaringTypeName.ShouldBe("OrderFixture");
    }
}
