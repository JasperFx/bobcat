namespace Bobcat.Engine;

public class ExecutionResults
{
    public string SpecId { get; }
    public DateTimeOffset StartTime { get; }
        
    public DateTimeOffset EndTime { get; set; }
        
        
    private readonly List<StepResult> _stepResults = new List<StepResult>();
    private readonly List<Type> _touchedTypes = new();

    public Counts Counts { get; } = new Counts();

    /// <summary>
    /// CLR types this execution observably touched — commands dispatched, events appended or
    /// emitted, aggregates arranged, messages sent, read models loaded — in first-touch order,
    /// deduplicated (issue #107). Recorded through <see cref="IStepContext.RecordTouchedType"/>
    /// by the code that saw the type cross the scenario's path; empty when nothing recorded.
    /// </summary>
    public IReadOnlyList<Type> TouchedTypes => _touchedTypes;

    /// <summary>Record an observed type; keeps first-touch order and ignores duplicates.</summary>
    public void Touch(Type type)
    {
        if (!_touchedTypes.Contains(type)) _touchedTypes.Add(type);
    }

    public IEnumerable<Exception> AllExceptions()
    {
        return _stepResults.SelectMany(x => x.AllExceptions());
    }
        
    public ExecutionResults(string specId, DateTimeOffset startTime)
    {
        SpecId = specId;
        StartTime = startTime;
    }

    public StepResult StartStep(string stepId, long elapsedMilliseconds, StepKind stepKind = StepKind.Then)
    {
        var result = new StepResult(stepId, elapsedMilliseconds, stepKind);
        _stepResults.Add(result);

        return result;
    }

    internal void Tabulate(StepResult result)
    {
        result.Tabulate(Counts);
    }

    public IReadOnlyList<StepResult> Steps => _stepResults;
}