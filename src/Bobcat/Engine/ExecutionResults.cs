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

    private readonly List<TimelinePoint> _timeline = new();

    /// <summary>
    /// Named stop points that are not steps — the <c>ResetAll</c> / <c>BeginScenarioAll</c>
    /// bracket, <c>BeforeEach</c>/<c>AfterEach</c>, <c>EndScenarioAll</c> — each with its offsets
    /// on the scenario's wall clock (issue #141). Steps carry their own offsets on
    /// <see cref="StepResult.Start"/>/<see cref="StepResult.End"/>, against the same zero: the
    /// moment the scenario was announced, before any reset ran. Time between stop points that
    /// nothing here owns is a gap a consumer can compute by subtraction.
    /// </summary>
    public IReadOnlyList<TimelinePoint> Timeline => _timeline;

    public void RecordTimelinePoint(string name, long startMs, long endMs)
        => _timeline.Add(new TimelinePoint(name, startMs, endMs));

    /// <summary>
    /// The scenario's true wall clock: announced start to the end of the whole bracket,
    /// <c>EndScenarioAll</c> included. Zero when the results were produced by something other
    /// than the runner's bracket (a bare <see cref="Executor"/> in a test, an older artifact) —
    /// a consumer falls back to the last step's end, knowing that under-reports.
    /// </summary>
    public long WallClockMs { get; set; }
}

/// <summary>
/// One named, non-step stop point on a scenario's timeline: milliseconds from the scenario's
/// announced start. Offsets rather than durations, deliberately — durations say what the work
/// cost, offsets also say what no work owns.
/// </summary>
public record TimelinePoint(string Name, long StartMs, long EndMs)
{
    public long DurationMs => Math.Max(0, EndMs - StartMs);
}