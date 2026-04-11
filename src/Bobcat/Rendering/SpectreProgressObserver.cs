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
