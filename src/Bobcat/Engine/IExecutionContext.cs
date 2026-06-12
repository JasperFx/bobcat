namespace Bobcat.Engine;

/// <summary>
/// Engine-internal context used by the Executor to manage lifecycle.
/// Extends IStepContext so it can be passed to steps (which only see the narrow interface).
/// </summary>
public interface IExecutionContext : IStepContext
{
    IEnumerable<Exception> Exceptions { get; }
    ExecutionResults Results { get; }

    /// <summary>
    /// Per-step relay the Executor installs around each step so that the step's
    /// <see cref="IStepContext.ReportProgress"/> calls reach the observer. Set while a step
    /// runs, null between steps.
    /// </summary>
    Action<StepUpdate>? ProgressSink { get; set; }
    void MarkCancelled(string reason);
    StepResult StepStarted(IExecutionStep step, long elapsedMilliseconds);
    void ExecutionStarted();
    void ExecutionFailed(Exception exception, long elapsedMilliseconds);
    void ExecutionFinished(long elapsedMilliseconds);
    void StepFinished(StepResult result);
    void TimedOut(long elapsedMilliseconds);
}
