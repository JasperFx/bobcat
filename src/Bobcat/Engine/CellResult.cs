namespace Bobcat.Engine;

public class CellResult
{
    public string Name { get; }
    public ResultStatus Status { get; }
    public string DisplayText { get; }

    public CellResult(string name, ResultStatus status, string displayText)
    {
        Name = name;
        Status = status;
        DisplayText = displayText;
    }

    public Exception? Exception { get; init; }

    /// <summary>
    /// Row index for set verification results (0-based). -1 for non-table cells.
    /// </summary>
    public int RowIndex { get; init; } = -1;
}

