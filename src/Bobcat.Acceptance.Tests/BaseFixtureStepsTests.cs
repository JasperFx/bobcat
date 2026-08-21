using Bobcat.Engine;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// Proves the #104 generator change: step methods declared on a base fixture are discovered and
/// bound, most-derived wins on duplicate step text, and feature-level @slice/@domain/Triggered-by
/// land on the FeatureDefinition and the scenario traits.
/// </summary>
public class BaseFixtureStepsTests
{
    [Fact]
    public async Task inherited_and_own_steps_run_together()
    {
        var results = await Specs.Run(Derived_Calculator_Feature.Define(), "Inherited and own steps run together");

        // 10 + 5 - 3 = 12 — Given/When from the base, "I subtract" from the derived.
        results.Step("the running total is 12").StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task most_derived_step_wins_on_duplicate_text()
    {
        var results = await Specs.Run(Derived_Calculator_Feature.Define(), "The derived step hides the base step of the same text");

        // The base Label returns true only for "base"; the derived override only for "derived".
        results.Step("the label is \"derived\"").StepStatus.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public void feature_level_slice_and_domain_tags_are_exposed()
    {
        var feature = Derived_Calculator_Feature.Define();

        feature.Domain.ShouldBe("Arithmetic");
        feature.TriggeredBy.ShouldBe("a base-class grammar");

        // @slice is on one scenario; it reaches that scenario's tags and its resilience traits.
        var labelling = feature.Scenarios.First(s => s.Title.StartsWith("The derived step"));
        labelling.Tags.ShouldContain("slice:Labelling");
        Resilience.ResilienceTags.ToTraits(labelling.Tags)["Slice"].ShouldBe("Labelling");
    }
}
