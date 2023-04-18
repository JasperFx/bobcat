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
}

