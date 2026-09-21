using Bobcat.Runtime;

namespace Bobcat.Engine;

public class SpecExecutionContext : IExecutionContext
{
    private readonly IServiceProvider? _services;
    private readonly TestResources? _resources;
    private readonly List<Exception> _exceptions = new();

    /// <summary>
    /// The currently executing step's result — Log() and AttachDiagnostic() route here.
    /// </summary>
    internal StepResult? CurrentStep { get; set; }

    public SpecExecutionContext(string specId, IServiceProvider? services = null, TestResources? resources = null)
    {
        SpecId = specId;
        _services = services;
        _resources = resources;
        Results = new ExecutionResults(specId, DateTimeOffset.UtcNow);
    }

    public string SpecId { get; }
    public ExecutionResults Results { get; }
    public CancellationToken Cancellation { get; set; }
    public Action<StepUpdate>? ProgressSink { get; set; }

    public IEnumerable<Exception> Exceptions => _exceptions;

    public T GetService<T>() where T : notnull
    {
        if (_services == null)
            throw new InvalidOperationException("No service provider configured.");

        return (T)(_services.GetService(typeof(T))
                   ?? throw new InvalidOperationException($"Service {typeof(T).Name} not registered."));
    }

    public T GetResource<T>(string? name = null) where T : class, ITestResource
    {
        if (_resources == null)
            throw new InvalidOperationException("No TestResources configured.");

        return _resources.GetResource<T>(name);
    }

    public void Log(string message)
    {
        CurrentStep?.AddLog(message);
    }

    public void AttachDiagnostic(string key, object data)
    {
        CurrentStep?.AttachDiagnostic(key, data);
    }

    public void ReportProgress(StepUpdate update)
    {
        ProgressSink?.Invoke(update);
    }

    public void RecordTouchedType(Type type)
    {
        Results.Touch(type);
    }

    public void MarkCancelled(string reason)
    {
    }

    public StepResult StepStarted(IExecutionStep step, long elapsedMilliseconds)
    {
        var result = Results.StartStep(step.StepId, elapsedMilliseconds, step.StepKind);
        CurrentStep = result;
        return result;
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
        CurrentStep = null;
    }

    public void StepFinished(StepResult result)
    {
        Results.Tabulate(result);
        CurrentStep = null;
    }

    public void TimedOut(long elapsedMilliseconds)
    {
    }
}
