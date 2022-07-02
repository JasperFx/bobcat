using System.Diagnostics;

namespace Bobcat.Model
{
    // TODO -- split the interface for what's valid to the execution step
    // and what's valid to the engine
    public interface IExecutionContext
    {
        string SpecId { get; }
        
        // Timings?
        // Record outputs?
        // Expose a service locator?
        // Track counts and results
        // Expose extended logging
        // Custom tracing for troubleshooting?
        // TODO -- tie into ILogger here, correlate logs captured to
        // the SpecResults

        IReadOnlyList<IExecutionStep> Steps { get; }
        
        TimeSpan Timeout { get; }
        
        IEnumerable<Exception> Exceptions { get; }
        void MarkCancelled(string reason);
        StepResult StepStarted(IExecutionStep step, long elapsedMilliseconds);
        void ExecutionStarted();
        void ExecutionFailed(Exception exception, long elapsedMilliseconds);
        void ExecutionFinished(long elapsedMilliseconds);
        void StepFinished(StepResult result);
        void TimedOut(long elapsedMilliseconds);
    }
}