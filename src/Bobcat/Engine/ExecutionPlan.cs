namespace Bobcat.Engine;

public class ExecutionPlan
{
    private readonly List<IExecutionStep> _steps = new List<IExecutionStep>();
        
    public string SpecId { get; }

    public ExecutionPlan(string specId, TimeSpan timeout)
    {
        SpecId = specId;
        Timeout = timeout;
    }

    public void Add(IExecutionStep step)
    {
        _steps.Add(step);    
    }

    public IReadOnlyList<IExecutionStep> Steps => _steps;
        
    public TimeSpan Timeout { get; }
}