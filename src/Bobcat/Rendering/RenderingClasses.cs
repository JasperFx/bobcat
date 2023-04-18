using Spectre.Console.Rendering;

namespace Bobcat.Rendering;

public interface ISpecWriter
{
    IRenderable Render();
}

public enum CellMode
{
    Input,
    Right,
    Wrong,
    Error,
    Text
}

public class Cell
{
    public string Text { get; }
    public CellMode Mode { get; }

    public Cell(string text, CellMode mode)
    {
        Text = text;
        Mode = mode;
    }
}


public class Line
{
    private readonly List<Cell> _cells = new List<Cell>();
    
    public Line(params Cell[] cells)
    {
        _cells.AddRange(cells);
    }

    public void AddCell(string text, CellMode mode)
    {
        AddCell(new Cell(text, mode));
    }

    public void AddCell(Cell cell)
    {
        _cells.Add(cell);
    }
}