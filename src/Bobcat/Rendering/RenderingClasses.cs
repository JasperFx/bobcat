using System.Collections;
using JasperFx.Core;
using Spectre.Console;

namespace Bobcat.Rendering;

public enum Mode
{
    Input,
    Right,
    Wrong,
    Error,
    Text
}

public enum TextAlign
{
    Left,
    Right
}

public record Cell(string Text, Mode Mode, TextAlign TextAlign = TextAlign.Left);

public class Line : IEnumerable<Cell>
{
    public Mode Mode { get; set; } = Mode.Text;
    public List<Cell> Cells { get; } = new();

    public void Add(string text, Mode mode)
    {
        Cells.Add(new Cell(text, mode));
    }

    public string? CommentText { get; set; }
    public string? ErrorText { get; set; }

    public IEnumerator<Cell> GetEnumerator() => Cells.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public class Table
{
    public string HeaderText { get; set; } = "Table Header";
    public List<string> Headers { get; } = new();
}
