namespace Bobcat.Model;

public class ExecutionResults
{
    public string SpecId { get; }
    public DateTimeOffset StartTime { get; }
        
    public DateTimeOffset EndTime { get; set; }
        
        
    private readonly List<StepResult> _stepResults = new List<StepResult>();

    public Counts Counts { get; } = new Counts();

    public IEnumerable<Exception> AllExceptions()
    {
        return _stepResults.SelectMany(x => x.AllExceptions());
    }
        
    public ExecutionResults(string specId, DateTimeOffset startTime)
    {
        SpecId = specId;
        StartTime = startTime;
    }

    public StepResult StartStep(string stepId, long elapsedMilliseconds)
    {
        var result = new StepResult(stepId, elapsedMilliseconds);
        _stepResults.Add(result);

        return result;
    }

    internal void Tabulate(StepResult result)
    {
        result.Tabulate(Counts);
    }

    public IReadOnlyList<StepResult> Steps => _stepResults;
}