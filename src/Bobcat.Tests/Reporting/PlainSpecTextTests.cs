using Bobcat.Engine;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Tests.Reporting;

/// <summary>
/// The plain-text rendering sent to a test runner through <see cref="SpecOutput"/> (issues #409,
/// #445).
/// </summary>
[Collection(ReportVisibilityCollection.Name)]
public class PlainSpecTextTests
{
    private const char escape = (char)27;

    private static SpecRender render(bool failing = false, bool reporting = false)
    {
        using var recording = ScenarioRecorder.Begin(
            "Appointments", "a proposal is confirmed", publisher: null, runId: Guid.NewGuid());

        if (reporting) SharedProducer.Report();

        using (ScenarioRecorder.Step("Then", "the appointment is confirmed"))
        {
            ScenarioRecorder.Note("confirmed as 9c21");

            if (failing)
            {
                ScenarioRecorder.CurrentStep!.Cells.Add(new CellResult("status", ResultStatus.failed)
                {
                    Expected = "Confirmed", Actual = "Proposed"
                });
            }
        }

        if (failing) recording.FailureDescription = "the appointment was not confirmed";

        // Read before disposal: Dispose is what closes the scenario and clears the ambient sink.
        return SpecRender.FromRecording(recording);
    }

    [Fact]
    public void a_scenario_renders_its_feature_its_steps_and_its_notes()
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
        var spec = render(failing: true, reporting: true);

        var written = new List<string>();
        using (SpecOutput.Open(written.Add)) SpecOutput.WriteScenario(spec);

        written.ShouldBe(PlainSpecText.Lines(spec));
    }

    [Fact]
    public void the_reports_are_separable_from_the_scenario_because_one_switch_asks_for_that()
    {
        // BOBCAT_SPEC_OUTPUT=0 leaves a destination with the reports only.
        var spec = render(failing: true, reporting: true);

        var reports = PlainSpecText.ReportLines(spec);
        reports.ShouldContain(l => l.Contains("Message activity"));

        PlainSpecText.ScenarioLines(spec).ShouldNotContain(l => l.Contains("Message activity"));
        PlainSpecText.Lines(spec).Count.ShouldBe(
            PlainSpecText.ScenarioLines(spec).Count + reports.Count);
    }
}
