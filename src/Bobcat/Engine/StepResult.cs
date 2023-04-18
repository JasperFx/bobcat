namespace Bobcat.Engine;

public class StepResult
{
    public string StepId { get; }

    public StepResult(string stepId, long start, ResultStatus status = ResultStatus.ok)
    {
        StepId = stepId;
        Start = start;
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