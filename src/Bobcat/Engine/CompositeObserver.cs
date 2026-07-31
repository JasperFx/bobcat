namespace Bobcat.Engine;

/// <summary>
/// Fans every <see cref="IExecutionObserver"/> callback out to N inner observers, so a monitor
/// publisher can ride alongside an MTP host's node publisher or the console renderer. One
/// observer throwing never starves the observers after it and never fails the run — observation
/// is strictly read-only with respect to the suite's outcome.
/// </summary>
public class CompositeObserver : IExecutionObserver
{
    private readonly IExecutionObserver[] _observers;

    public CompositeObserver(params IExecutionObserver[] observers)
    {
        _observers = observers;
    }

    public IReadOnlyList<IExecutionObserver> Observers => _observers;

    private void each(Action<IExecutionObserver> callback)
    {
        foreach (var observer in _observers)
        {
            try
            {
                callback(observer);
            }
            catch
            {
                // Swallowed on purpose: a broken observer must not break the run or the
                // observers behind it. There is no logger at this altitude to report to.
            }
        }
    }

    public void RunStarted(int totalScenarios) => each(o => o.RunStarted(totalScenarios));
    public void RunFinished(Runtime.SuiteResults results) => each(o => o.RunFinished(results));
    public void FeatureStarted(string featureTitle) => each(o => o.FeatureStarted(featureTitle));
    public void FeatureFinished(string featureTitle) => each(o => o.FeatureFinished(featureTitle));
    public void ScenarioStarted(string featureTitle, string scenarioTitle) => each(o => o.ScenarioStarted(featureTitle, scenarioTitle));
    public void StepStarted(string stepId, StepKind kind, string stepText) => each(o => o.StepStarted(stepId, kind, stepText));
    public void StepProgress(string stepId, StepUpdate update) => each(o => o.StepProgress(stepId, update));
    public void StepFinished(StepResult result) => each(o => o.StepFinished(result));
    public void ScenarioFinished(ExecutionResults results) => each(o => o.ScenarioFinished(results));
    public void ScenarioRetrying(string scenarioTitle, int nextAttempt, string reason) => each(o => o.ScenarioRetrying(scenarioTitle, nextAttempt, reason));
    public void ScenarioCompleted(string featureTitle, Runtime.ScenarioResult result) => each(o => o.ScenarioCompleted(featureTitle, result));
}
