namespace Bobcat.Engine;

public class SpecExecutionContext : IExecutionContext
{
    private readonly IServiceProvider? _services;
    private readonly List<Exception> _exceptions = new();
    private readonly List<string> _log = new();

    public SpecExecutionContext(string specId, IServiceProvider? services = null)
    {
        SpecId = specId;
        _services = services;
        Results = new ExecutionResults(specId, DateTimeOffset.UtcNow);
    }

    public string SpecId { get; }
    public ExecutionResults Results { get; }
    public CancellationToken Cancellation { get; set; }

    public IEnumerable<Exception> Exceptions => _exceptions;

    public T GetService<T>() where T : notnull
    {
        if (_services == null)
            throw new InvalidOperationException("No service provider configured. Register an ISystem to provide services.");

        return (T)(_services.GetService(typeof(T))
                   ?? throw new InvalidOperationException($"Service {typeof(T).Name} not registered."));
    }

    public void Log(string message)
    {
        _log.Add(message);
    }

    public void AttachDiagnostic(string key, object data)
    {
        // Diagnostics bag — will be expanded in Phase 3
    }

    public void MarkCancelled(string reason)
    {
        // Could track the cancellation reason for rendering
    }

    public StepResult StepStarted(IExecutionStep step, long elapsedMilliseconds)
    {
        return Results.StartStep(step.StepId, elapsedMilliseconds, step.StepKind);
    }

    public void ExecutionStarted()
    {
    }

    public void ExecutionFailed(Exception exception, long elapsedMilliseconds)
    {
        _exceptions.Add(exception);
        Results.EndTime = DateTimeOffset.UtcNow;
    }

    public void ExecutionFinished(long elapsedMilliseconds)
    {
        Results.EndTime = DateTimeOffset.UtcNow;
    }

    public void StepFinished(StepResult result)
    {
        Results.Tabulate(result);
    }

    public void TimedOut(long elapsedMilliseconds)
    {
    }
}
