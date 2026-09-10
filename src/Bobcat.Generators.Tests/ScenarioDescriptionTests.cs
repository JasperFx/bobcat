using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// Issue #258: free text between <c>Scenario:</c> and its first step is the scenario's description
/// (standard Gherkin). The parser used to drop it, which left the feature-level description as
/// the only place a slice's <c>Triggered by</c> could live — and one feature-level line was then
/// stamped on every slice the feature held.
/// </summary>
public class ScenarioDescriptionTests
{
    private static FeatureInfo parse(string gherkin) => SimpleGherkinParser.Parse(gherkin, "Test.feature")!;

    [Fact]
    public void free_text_before_the_first_step_is_the_scenarios_description()
    {
        var feature = parse("""
            Feature: Appointments
              Triggered by the shelter

              Scenario: A proposal arrives
                Triggered by HomeCheckAssignmentAccepted
                Some prose about it
                Given no events for Appointment "1"
                When it happens
            """);

        var scenario = feature.Scenarios.ShouldHaveSingleItem();
        scenario.Description.ShouldBe("Triggered by HomeCheckAssignmentAccepted\nSome prose about it");
        scenario.Steps.Count.ShouldBe(2);

        // The feature keeps its own, unchanged.
        feature.Description.ShouldBe("Triggered by the shelter");
    }

    [Fact]
    public void a_scenario_without_free_text_has_no_description()
    {
        var feature = parse("""
            Feature: Appointments
              Triggered by the shelter

              Scenario: Plain
                Given something
            """);

        feature.Scenarios.ShouldHaveSingleItem().Description.ShouldBeNull();
    }

    [Fact]
    public void background_steps_do_not_end_a_scenarios_description_before_it_starts()
    {
        // Background steps are cloned into the scenario's step list up front, so "no steps yet"
        // cannot be read off the list — the description must still be collected.
        var feature = parse("""
            Feature: Appointments

              Background:
                Given a proposed home check

              Scenario: Confirming
                Triggered by the owner
                When it is confirmed
            """);

        var scenario = feature.Scenarios.ShouldHaveSingleItem();
        scenario.Description.ShouldBe("Triggered by the owner");
        scenario.Steps.Select(s => s.Text).ShouldBe(["a proposed home check", "it is confirmed"]);
    }

    [Fact]
    public void every_example_of_an_outline_carries_the_outlines_description()
    {
        var feature = parse("""
            Feature: Appointments

              Scenario Outline: Approving <kind>
                Triggered by an approval
                When <kind> is approved

                Examples:
                  | kind      |
                  | foster    |
                  | surrender |
            """);

        feature.Scenarios.Count.ShouldBe(2);
        feature.Scenarios.ShouldAllBe(s => s.Description == "Triggered by an approval");
    }
}
