using Bobcat.Engine;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Tests.Reporting;

/// <summary>
/// The plain-text rendering both lanes send to a test runner (issues #409, #445): the projected
/// lane through <see cref="SpecOutput"/>, the Gherkin lane onto its MTP test node.
/// </summary>
[Collection(ReportVisibilityCollection.Name)]
public class PlainSpecTextTests
{
    private const char escape = (char)27;

    private static SpecRender render(bool failing = false, bool reporting = false)
    {
        var results = new ExecutionResults("a proposal is confirmed", DateTimeOffset.UtcNow);

        using (ScenarioReports.Open(results))
        {
            if (reporting) SharedProducer.Report();

            var step = results.StartStep("s1", 0);
            step.StepText = "the appointment is confirmed";
            step.AddLog("confirmed as 9c21");

            if (failing)
            {
                step.MarkCells(new CellResult("status", ResultStatus.failed)
                {
                    Expected = "Confirmed", Actual = "Proposed"
                });
                results.Counts.Wrongs++;
            }
            else
            {
                results.Counts.Rights++;
            }

            step.MarkSuccess();
        }

        return SpecRender.FromResults("a proposal is confirmed", results, "Appointments");
    }

    [Fact]
    public void a_scenario_renders_its_feature_its_steps_and_what_a_step_logged()
    {
        var lines = PlainSpecText.Lines(render());

        lines.ShouldContain("Feature: Appointments");
        lines.ShouldContain(l => l.Contains("the appointment is confirmed"));
        lines.ShouldContain(l => l.Contains("confirmed as 9c21"));
    }

    [Fact]
    public void the_rendering_is_text_a_ci_log_and_grep_can_read()
    {
        PlainSpecText.Lines(render(failing: true, reporting: true))
            .ShouldAllBe(l => !l.Contains(escape));
    }

    [Fact]
    public void the_projected_lane_writes_this_rendering_rather_than_a_copy_of_it()
    {
        // The guard that matters: two lanes send a scenario to two destinations, and this is what
        // keeps a change to one from reaching that lane and silently skipping the other.
        var spec = render(failing: true, reporting: true);

        var written = new List<string>();
        using (SpecOutput.Open(written.Add)) SpecOutput.WriteScenario(spec);

        written.ShouldBe(PlainSpecText.Lines(spec));
    }

    [Fact]
    public void the_reports_are_separable_from_the_scenario_because_one_switch_asks_for_that()
    {
        // BOBCAT_SPEC_OUTPUT=0 leaves a destination with the reports only, in either lane.
        var spec = render(failing: true, reporting: true);

        var reports = PlainSpecText.ReportLines(spec);
        reports.ShouldContain(l => l.Contains("Message activity"));

        PlainSpecText.ScenarioLines(spec).ShouldNotContain(l => l.Contains("Message activity"));
        PlainSpecText.Lines(spec).Count.ShouldBe(
            PlainSpecText.ScenarioLines(spec).Count + reports.Count);
    }
}
