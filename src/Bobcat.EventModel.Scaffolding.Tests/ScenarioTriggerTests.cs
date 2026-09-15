using Bobcat.EventModel;
using Bobcat.EventModel.Scaffolding;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issue #258: a slice is scenario-level, and so is its trigger. The scaffolder used to write the
/// <em>first</em> slice's label at feature level, which the generator then stamped on every slice
/// in the feature — nine wrong trigger labels on the CritterCrush canvas from one line.
/// </summary>
public class ScenarioTriggerTests
{
    private static string featureFor(string firstLabel, string secondLabel, string? firstChapter = null, string? secondChapter = null)
    {
        var reading = CuratedModelReader.Read($$"""
            schema: 1
            model: Shelter
            namespace: Shelter
            slices:
              - name: ProposeAppointment
                pattern: Command
                {{(firstChapter is null ? "" : $"chapter: {firstChapter}")}}
                trigger: { kind: Http, label: {{firstLabel}} }
                command: ProposeAppointment
                aggregates: [Appointment]
                events: [AppointmentProposed]
                specifications:
                  feature: Appointments
                  scenarios:
                    - name: A proposal is recorded
                      when: { command: ProposeAppointment }
                      then: [{ event: AppointmentProposed }]
              - name: ConfirmAppointment
                pattern: Command
                {{(secondChapter is null ? "" : $"chapter: {secondChapter}")}}
                trigger: { kind: Http, label: {{secondLabel}} }
                command: ConfirmAppointment
                aggregates: [Appointment]
                events: [AppointmentConfirmed]
                specifications:
                  feature: Appointments
                  scenarios:
                    - name: A proposal is confirmed
                      when: { command: ConfirmAppointment }
                      then: [{ event: AppointmentConfirmed }]
            """);
        reading.Problems.ShouldBeEmpty();

        return SliceScaffolder.ScaffoldFeatures(reading.File!).Single().Value;
    }

    private static string[] linesOf(string feature) => feature.Split('\n').Select(x => x.TrimEnd('\r')).ToArray();

    [Fact]
    public void slices_with_different_triggers_each_declare_theirs_on_their_scenarios()
    {
        var lines = linesOf(featureFor("Appointments queue", "My appointments"));

        lines.ShouldContain("    Triggered by Appointments queue");
        lines.ShouldContain("    Triggered by My appointments");

        // No feature-level line: whichever label it carried would be wrong for the other slice.
        lines.ShouldNotContain(x => x.StartsWith("  Triggered by"));
    }

    // ---- issue #298: the chapter follows the trigger's rule -------------------------------------

    [Fact]
    public void slices_that_share_a_chapter_get_one_feature_level_tag()
    {
        var lines = linesOf(featureFor("Q", "Q", "Appointments", "Appointments"));

        lines.Count(x => x == "@chapter:Appointments").ShouldBe(1);
        lines.ShouldNotContain(x => x.Contains("@slice:") && x.Contains("@chapter:"));
    }

    [Fact]
    public void slices_in_different_chapters_each_tag_their_own_scenarios()
    {
        var lines = linesOf(featureFor("Q", "Q", "Proposals", "Confirmations"));

        lines.ShouldNotContain(x => x.StartsWith("@chapter:"));
        lines.ShouldContain("  @slice:ProposeAppointment @chapter:Proposals");
        lines.ShouldContain("  @slice:ConfirmAppointment @chapter:Confirmations");
    }

    [Fact]
    public void a_model_with_no_chapters_writes_no_chapter_tag_at_all()
    {
        linesOf(featureFor("Q", "Q")).ShouldNotContain(x => x.Contains("@chapter:"));
    }

    [Fact]
    public void slices_that_share_a_trigger_keep_one_feature_level_line()
    {
        var lines = linesOf(featureFor("Appointments queue", "Appointments queue"));

        lines.Count(x => x.StartsWith("  Triggered by Appointments queue")).ShouldBe(1);
        lines.ShouldNotContain(x => x.StartsWith("    Triggered by"));
    }
}
