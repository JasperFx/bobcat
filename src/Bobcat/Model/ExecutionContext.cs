namespace Bobcat.Model;

public class ExecutionContext 
{
    public ExecutionContext(string specId, TimeSpan timeout, IEnumerable<IExecutionStep> steps)
    {
        SpecId = specId;
        Steps = steps.ToArray();
        Timeout = timeout;
    }

    public string SpecId { get; }
    public IReadOnlyList<IExecutionStep> Steps { get; }
    public TimeSpan Timeout { get; }


}