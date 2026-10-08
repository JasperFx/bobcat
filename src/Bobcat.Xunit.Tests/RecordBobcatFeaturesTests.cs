using Shouldly;
using Xunit;

namespace Bobcat.Xunit.Tests;

/// <summary>
/// <c>[BobcatFeature]</c> alone records, through the assembly-level hook this project applies the
/// way the package does — the one-attribute feature class.
/// </summary>
[BobcatFeature("Feature only")]
public class FeatureOnlyTests
{
    [Fact]
    public void a_plain_fact_in_a_feature_class_is_recorded()
        => ScenarioRecorder.Current.ShouldNotBeNull().Uid
            .ShouldBe("Feature only/a plain fact in a feature class is recorded");

    [Fact]
    public void its_steps_land_on_the_scenario()
    {
        using (ScenarioRecorder.Step("Given", "nothing but [BobcatFeature]"))
        {
        }

        ScenarioRecorder.Current!.Steps.ShouldHaveSingleItem()
            .ToString().ShouldBe("Given nothing but [BobcatFeature]");
    }
}

/// <summary>A class that is not a feature is left alone: no recording opens.</summary>
public class NotAFeatureTests
{
    [Fact]
    public void no_scenario_is_open() => ScenarioRecorder.Current.ShouldBeNull();
}

public class RecordBobcatFeaturesAppliesTests
{
    [Fact]
    public void a_feature_class_is_recorded_by_the_hook()
        => RecordBobcatFeaturesAttribute.Applies(typeof(FeatureOnlyTests),
                typeof(FeatureOnlyTests).GetMethod(nameof(FeatureOnlyTests.a_plain_fact_in_a_feature_class_is_recorded))!)
            .ShouldBeTrue();

    [Fact]
    public void a_class_that_opens_its_own_scenario_is_left_to_it()
        // LiveScenarioBracketTests carries [BobcatScenario]; a second bracket would nest a recording.
        => RecordBobcatFeaturesAttribute.Applies(typeof(LiveScenarioBracketTests),
                typeof(LiveScenarioBracketTests).GetMethods().First(m => m.Name.StartsWith("a_")))
            .ShouldBeFalse();

    [Fact]
    public void a_class_without_the_feature_attribute_is_not_recorded()
        => RecordBobcatFeaturesAttribute.Applies(typeof(NotAFeatureTests),
                typeof(NotAFeatureTests).GetMethod(nameof(NotAFeatureTests.no_scenario_is_open))!)
            .ShouldBeFalse();
}
