namespace Bobcat.Engine;

public class StepResult
{
    public string StepId { get; }
    public StepKind StepKind { get; }
    public FailureLevel FailureLevel { get; private set; } = FailureLevel.None;

    /// <summary>
    /// Original Gherkin step text for rendering (e.g., "the left operand is 25").
    /// </summary>
    public string? StepText { get; set; }

    public StepResult(string stepId, long start, StepKind stepKind = StepKind.Then, ResultStatus status = ResultStatus.ok)
    {
        StepId = stepId;
        Start = start;
        StepKind = stepKind;
        StepStatus = status;
    }

    public Exception? Exception { get; private set; }

    public IEnumerable<Exception> AllExceptions()
    {
        if (Exception != null) yield return Exception;

        foreach (var cellResult in _cells.Where(x => x.Exception != null))
        {
            yield return cellResult.Exception!;
        }
    }

    public ResultStatus StepStatus { get; private set; }

    public StepResult MarkErrored(Exception ex, long end)
    {
        Exception = ex;
        StepStatus = ResultStatus.error;
        End = end;

        FailureLevel = ex switch
        {
            SpecCatastrophicException => FailureLevel.Catastrophic,
            SpecCriticalException => FailureLevel.Critical,
            _ => StepKind == StepKind.Then ? FailureLevel.Critical : FailureLevel.Critical
        };

        return this;
    }

    public StepResult MarkFailed()
    {
        StepStatus = ResultStatus.failed;
        FailureLevel = FailureLevel.Assertion;
        return this;
    }

    internal void Tabulate(Counts counts)
    {
        counts.Read(StepStatus);
        foreach (var cell in _cells)
        {
            counts.Read(cell.Status);
        }
    }

    /// <summary>
    /// When true, cells represent a set verification table and should be
    /// rendered as a table with per-cell coloring.
    /// </summary>
    public bool IsSetVerification { get; set; }

    /// <summary>
    /// Column headers for set verification table rendering.
    /// </summary>
    public IReadOnlyList<string>? SetVerificationColumns { get; set; }

    public long Start { get; }
    public long End { get; private set; }

    private readonly List<CellResult> _cells = new();

    public IReadOnlyList<CellResult> Cells => _cells;

    public StepResult MarkCells(params CellResult[] cells)
    {
        _cells.AddRange(cells);
        return this;
    }

    public void MarkEnded(long end)
    {
        End = end;
    }

    public void MarkSuccess()
    {
        StepStatus = ResultStatus.success;
    }
}