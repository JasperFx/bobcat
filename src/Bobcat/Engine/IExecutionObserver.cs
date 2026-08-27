namespace Bobcat.Engine;

/// <summary>
/// Receives notifications during execution for progress display and result collection.
/// Implementations must be thread-safe if used with concurrent execution.
/// </summary>
public interface IExecutionObserver
{
    /// <summary>
    /// The whole run is starting — fired once, before resources start, with the count of
    /// scenarios that will run after filtering. Default no-op so existing observers are
    /// unaffected.
    /// </summary>
    void RunStarted(int totalScenarios) { }

    /// <summary>
    /// The whole run is over — fired once with the aggregated results, including on a
    /// preflight failure (in which case no feature ran). Default no-op.
    /// </summary>
    void RunFinished(Runtime.SuiteResults results) { }

    void FeatureStarted(string featureTitle);
    void FeatureFinished(string featureTitle);
    void ScenarioStarted(string featureTitle, string scenarioTitle);

    /// <summary>
    /// <see cref="ScenarioStarted(string,string)"/> plus the number of steps the plan will run
    /// — known before the first step starts, because the plan is built before the scenario is
    /// announced. This is what turns "step 3 is running" into "step 3 of 9". The default
    /// forwards to the two-argument form, so an observer that does not care about the count
    /// is unaffected; the runner always calls this one.
    /// </summary>
    void ScenarioStarted(string featureTitle, string scenarioTitle, int totalSteps)
        => ScenarioStarted(featureTitle, scenarioTitle);

    void StepStarted(string stepId, StepKind kind, string stepText);

    /// <summary>
    /// <see cref="StepStarted(string,StepKind,string)"/> plus the step's offset on the
    /// scenario's wall clock (issue #141) — the executor's own stamp, the same value that
    /// lands on <see cref="StepResult.Start"/>, so an observer never needs a second clock
    /// that almost agrees with the report. The default forwards to the three-argument form;
    /// the executor always calls this one.
    /// </summary>
    void StepStarted(string stepId, StepKind kind, string stepText, long scenarioElapsedMs)
        => StepStarted(stepId, kind, stepText);

    /// <summary>
    /// Interim progress raised by a running step before it finishes — partial results or a
    /// short status message. Renderers should update the step's live row in place. May be
    /// called any number of times (including zero) between StepStarted and StepFinished.
    /// </summary>
    void StepProgress(string stepId, StepUpdate update);

    void StepFinished(StepResult result);
    void ScenarioFinished(ExecutionResults results);

    /// <summary>
    /// A scenario failed and is about to be attempted again. Renderers should make this
    /// visible as it happens — a retry that only shows up in the final summary reads as a
    /// clean pass while the run is in progress.
    /// </summary>
    /// <param name="scenarioTitle">The scenario being retried.</param>
    /// <param name="nextAttempt">1-based number of the attempt about to start.</param>
    /// <param name="reason">The policy's explanation for retrying.</param>
    void ScenarioRetrying(string scenarioTitle, int nextAttempt, string reason) { }

    /// <summary>
    /// A scenario is finished for good — all attempts done, disposition settled. Unlike
    /// <see cref="ScenarioFinished"/>, which fires once per attempt with only that attempt's
    /// results, this carries the whole <see cref="Runtime.ScenarioResult"/> including the
    /// attempt history and <see cref="Resilience.RunOutcome"/>. A front-end reporting one
    /// result per test — an MTP host, say — wants this one.
    /// </summary>
    void ScenarioCompleted(string featureTitle, Runtime.ScenarioResult result) { }
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
