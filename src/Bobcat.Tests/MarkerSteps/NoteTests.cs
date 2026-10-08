using Bobcat.Rendering;
using Shouldly;
using Spectre.Console;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>Free text in a specification, with no verdict of its own.</summary>
public class NoteTests
{
    [Fact]
    public void a_note_is_recorded_under_its_own_keyword_and_does_not_close_the_block()
    {
        using var recording = ScenarioRecorder.Begin("Feature", "Scenario", null, Guid.NewGuid());

        using (ScenarioRecorder.Step("Given", "a")) { }
        ScenarioRecorder.Note("why the second given matters");
        using (ScenarioRecorder.Step("Given", "b")) { }

        recording.Steps.Select(x => x.Keyword).ShouldBe(["Given", "Note", "And"]);
    }

    [Fact]
    public void a_note_renders_as_text_with_no_verdict()
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Out = new AnsiConsoleOutput(writer),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });

        ScenarioRecorder.Recording recording;
        using (recording = ScenarioRecorder.Begin("Feature", "Scenario", null, Guid.NewGuid()))
        {
            ScenarioRecorder.Note("the timer is scheduled, not sent");
        }

        new CommandLineRenderer(console).Render(SpecRender.FromRecording(recording));

        var line = writer.ToString().Split('\n').Single(x => x.Contains("the timer"));
        line.Trim().ShouldBe("» the timer is scheduled, not sent");
    }
}
