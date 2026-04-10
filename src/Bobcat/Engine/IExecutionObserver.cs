namespace Bobcat.Engine;

/// <summary>
/// Receives notifications during execution for progress display and result collection.
/// Implementations must be thread-safe if used with concurrent execution.
/// </summary>
public interface IExecutionObserver
{
    void FeatureStarted(string featureTitle);
    void FeatureFinished(string featureTitle);
    void ScenarioStarted(string featureTitle, string scenarioTitle);
    void StepStarted(string stepId, StepKind kind, string stepText);
    void StepFinished(StepResult result);
    void ScenarioFinished(ExecutionResults results);
}

/// <summary>
/// No-op observer for when progress reporting isn't needed.
/// </summary>
public class NullObserver : IExecutionObserver
{
    public static readonly NullObserver Instance = new();

    public void FeatureStarted(string featureTitle) { }
    public void FeatureFinished(string featureTitle) { }
    public void ScenarioStarted(string featureTitle, string scenarioTitle) { }
    public void StepStarted(string stepId, StepKind kind, string stepText) { }
    public void StepFinished(StepResult result) { }
    public void ScenarioFinished(ExecutionResults results) { }
}
