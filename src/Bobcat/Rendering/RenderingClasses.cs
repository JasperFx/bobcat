using System.Collections;
using JasperFx.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Bobcat.Rendering;

public class CommandLineRenderer
{
    public void Render(IEnumerable<IRenderable> elements)
    {
        
    }

    public void Render(IRenderable renderable)
    {
        if (renderable is Line line)
        {
            AnsiConsole.MarkupLine(line.Cells.Select(ToMarkdown).Join(""));
        }
    }

    public static string ToMarkdown(Cell cell)
    {
        switch (cell.Mode)
        {
            case Mode.Text:
                return cell.Text;
            case Mode.Input:
                return $"[italic]{cell.Text}[/]";
            case Mode.Right:
                return $"[green italic]{cell.Text}[/]";
            case Mode.Error:
                return $"[yellow italic]{cell.Text}[/]";
            case Mode.Wrong:
                return $"[red italic]{cell.Text}[/]";
        }

        return cell.Text;
    }
}

public interface IRenderable{}

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

public class Line : IEnumerable<Cell>, IRenderable
{
    public Mode Mode { get; set; } = Mode.Text;
    public List<Cell> Cells { get; } = new();

    public void Add(string text, Mode mode)
    {
        Cells.Add(new Cell(text, mode));
    }
    
    public string? CommentText { get; set; }
    public string? ErrorText { get; set; }
    public IEnumerator<Cell> GetEnumerator()
    {
        return Cells.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

public class Table : IRenderable
{

    public string HeaderText { get; set; } = "Table Header";
    public List<string> Headers { get; } = new();
    
    
}

