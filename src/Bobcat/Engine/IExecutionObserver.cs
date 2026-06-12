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

    /// <summary>
    /// Interim progress raised by a running step before it finishes — partial results or a
    /// short status message. Renderers should update the step's live row in place. May be
    /// called any number of times (including zero) between StepStarted and StepFinished.
    /// </summary>
    void StepProgress(string stepId, StepUpdate update);

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
    public void StepProgress(string stepId, StepUpdate update) { }
    public void StepFinished(StepResult result) { }
    public void ScenarioFinished(ExecutionResults results) { }
}
