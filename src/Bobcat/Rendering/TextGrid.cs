using System.Text;

namespace Bobcat.Rendering;

/// <summary>
/// A scenario report as plain monospaced text (issue #409) — the form that reaches a test runner's
/// own per-test output, where ANSI markup would arrive as escape codes rather than as colour.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second renderer rather than reusing the Spectre one.</b>
/// <see cref="CommandLineRenderer.RenderSetVerification"/> writes to a console it owns, with
/// markup. <c>ITestOutputHelper</c> and TUnit's writer are plain text captured by the platform and
/// shown in a results pane or a CI log, so the markup would be noise in exactly the place a
/// failure is read. Same model, two outputs — the split <see cref="SpecRender"/> exists for.
/// </para>
/// <para>
/// <b>Readable by a person and by an agent, from one rendering.</b> A fixed-width grid is what a
/// person scans and what a language model tokenizes without having to parse prose; the machine
/// surface with structure is the JSON report, and this is deliberately not a third format with its
/// own rules.
/// </para>
/// </remarks>
public static class TextGrid
{
    /// <summary>A marker on the rows that carry a verdict, so a failure is findable in a log tail.</summary>
    private const string FailedMarker = "  <-- FAILED";

    /// <summary>The report as lines, heading first. Empty when the report has no rows.</summary>
    public static IReadOnlyList<string> Render(ReportRender report)
    {
        var rows = report.Grid.Rows;
        if (rows.Count == 0) return [];

        var columns = report.Grid.Columns.Count > 0
            ? report.Grid.Columns
            : rows.SelectMany(r => r.Cells).Select(c => c.Column).Distinct().ToList();

        // Every cell's text, by row and column, so widths are measured once over what will
        // actually be printed rather than guessed from the values.
        var cells = rows
            .Select(row => columns
                .Select(column => row.Cells.FirstOrDefault(c => c.Column == column)?.DisplayText ?? "")
                .ToList())
            .ToList();

        var widths = columns
            .Select((column, i) => Math.Max(column.Length, cells.Max(row => row[i].Length)))
            .ToList();

        var lines = new List<string> { report.Title };

        lines.Add(join(columns.Select((c, i) => c.PadRight(widths[i]))));
        lines.Add(join(widths.Select(w => new string('-', w))));

        for (var r = 0; r < rows.Count; r++)
        {
            var line = join(cells[r].Select((text, i) => text.PadRight(widths[i])));

            // Marked rather than coloured: the row that disagreed has to be findable by eye in a
            // CI log and by a grep, neither of which sees an ANSI code.
            lines.Add(rows[r].AllCellsOk ? line : line + FailedMarker);
        }

        if (report.SuppressedRows > 0)
        {
            lines.Add($"…and {report.SuppressedRows} more "
                + $"{(report.SuppressedRows == 1 ? "row" : "rows")} not shown");
        }

        return lines;
    }

    /// <summary>The report as one block of text, for a sink that takes whole strings.</summary>
    public static string RenderToString(ReportRender report)
    {
        var builder = new StringBuilder();
        foreach (var line in Render(report)) builder.AppendLine(line);
        return builder.ToString();
    }

    private static string join(IEnumerable<string> parts) => string.Join(" | ", parts).TrimEnd();
}
