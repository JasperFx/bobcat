using Bobcat.Rendering;

namespace Bobcat.Projected.Acceptance.Tests;

/// <summary>
/// Runs a block of steps inside a projected scenario and hands back what it recorded: the
/// recording itself, and the same render a report or the JSON output is built from.
/// </summary>
/// <remarks>
/// The projected twin of <c>Bobcat.Acceptance.Tests/Specs.Run</c>. There is no plan and no executor:
/// the steps are ordinary method calls, the generated interceptors open and close each one, and the
/// recorder is the only thing holding the scenario.
/// </remarks>
public static class Projected
{
    public static (ScenarioRecorder.Recording Recording, SpecRender Render) Run(string scenario, Action steps)
    {
        using var recording = ScenarioRecorder.Begin("Projected Acceptance", scenario, null, Guid.NewGuid());
        steps();
        return (recording, SpecRender.FromRecording(recording));
    }

    public static async Task<(ScenarioRecorder.Recording Recording, SpecRender Render)> RunAsync(string scenario,
        Func<Task> steps)
    {
        using var recording = ScenarioRecorder.Begin("Projected Acceptance", scenario, null, Guid.NewGuid());
        await steps();
        return (recording, SpecRender.FromRecording(recording));
    }

    /// <summary>The recorded step whose text contains <paramref name="textContains"/>.</summary>
    public static ScenarioRecorder.RecordedStep Step(this ScenarioRecorder.Recording recording, string textContains)
        => recording.Steps.FirstOrDefault(s => s.Text.Contains(textContains))
           ?? throw new ArgumentException(
               $"No step matching '{textContains}'. Steps: {string.Join(" | ", recording.Steps.Select(s => s.Text))}");

    /// <summary>The rendered step whose text contains <paramref name="textContains"/>.</summary>
    public static StepRender Step(this SpecRender render, string textContains)
        => render.Steps.FirstOrDefault(s => s.StepText.Contains(textContains))
           ?? throw new ArgumentException(
               $"No step matching '{textContains}'. Steps: {string.Join(" | ", render.Steps.Select(s => s.StepText))}");
}
