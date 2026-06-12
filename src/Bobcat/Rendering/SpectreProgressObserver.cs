using Bobcat.Engine;
using Spectre.Console;

namespace Bobcat.Rendering;

/// <summary>
/// IExecutionObserver that shows live progress via Spectre.Console.
/// Displays current feature/scenario/step with a spinner during execution.
/// </summary>
public class SpectreProgressObserver : IExecutionObserver
{
    private string _currentFeature = "";
    private string _currentScenario = "";
    private string _currentStepText = "";
    private int _stepCount;
    private int _passCount;
    private int _failCount;

    public void FeatureStarted(string featureTitle)
    {
        _currentFeature = featureTitle;
        _stepCount = 0;
        _passCount = 0;
        _failCount = 0;
        AnsiConsole.MarkupLine($"[bold]Feature: {Markup.Escape(featureTitle)}[/]");
    }

    public void FeatureFinished(string featureTitle)
    {
        AnsiConsole.WriteLine();
    }

    public void ScenarioStarted(string featureTitle, string scenarioTitle)
    {
        _currentScenario = scenarioTitle;
        _stepCount = 0;
    }

    public void StepStarted(string stepId, StepKind kind, string stepText)
    {
        _stepCount++;
        _currentStepText = stepText;
    }

    public void StepProgress(string stepId, StepUpdate update)
    {
        // Stream interim feedback so a long-running step shows a live status instead of a frozen
        // line. The full console renderer will move this into an in-place Live region; for now we
        // emit a dim status line per update, which the carriage-return-free console preserves.
        var label = string.IsNullOrEmpty(_currentStepText) ? stepId : _currentStepText;

        if (!string.IsNullOrEmpty(update.Message))
        {
            AnsiConsole.MarkupLine($"[grey]  → {Markup.Escape(label)}: {Markup.Escape(update.Message!)}[/]");
        }

        foreach (var cell in update.Cells)
        {
            AnsiConsole.MarkupLine($"[grey]  → {Markup.Escape(label)}: {Markup.Escape(cell.DisplayText)}[/]");
        }
    }

    public void StepFinished(StepResult result)
    {
        if (result.StepStatus == ResultStatus.success)
            _passCount++;
        else if (result.StepStatus == ResultStatus.failed || result.StepStatus == ResultStatus.error)
            _failCount++;
    }

    public void ScenarioFinished(ExecutionResults results)
    {
    }
}
