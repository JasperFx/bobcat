namespace Bobcat;

/// <summary>
/// A step's whole data table, handed to a step method as one argument. Declare a parameter of
/// this type on a <c>[Given]</c>/<c>[When]</c>/<c>[Then]</c> helper and the caller passes the table —
/// headers and rows as written, usually as a table literal (see <see cref="Parse"/>) — in one
/// step rather than calling the helper once per row.
/// </summary>
/// <remarks>
/// <see cref="Bobcat.Runtime.TableRunner"/> builds rows into objects or calls a row method per row,
/// and <see cref="Bobcat.Runtime.SetVerificationComparer"/> compares a table of expected rows
/// against an actual collection.
/// </remarks>
public sealed class StepTable
{
    public StepTable(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        Headers = headers;
        Rows = rows;
    }

    /// <summary>The header cells, in order, exactly as written.</summary>
    public IReadOnlyList<string> Headers { get; }

    /// <summary>The data rows (excluding the header), each cell exactly as written.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    public int Count => Rows.Count;

    /// <summary>True when the header list contains <paramref name="header"/> (case-insensitive).</summary>
    public bool HasColumn(string header)
        => Headers.Any(h => string.Equals(h, header, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Each row as a header → cell dictionary (case-insensitive keys). A row shorter than the
    /// header list simply lacks the trailing keys.
    /// </summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> AsDictionaries()
    {
        var list = new List<IReadOnlyDictionary<string, string>>(Rows.Count);
        foreach (var row in Rows)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < Headers.Count && i < row.Count; i++)
                dict[Headers[i]] = row[i];
            list.Add(dict);
        }

        return list;
    }

    /// <summary>The cell at (<paramref name="row"/>, <paramref name="header"/>), or null when absent.</summary>
    public string? Cell(int row, string header)
    {
        if (row < 0 || row >= Rows.Count) return null;
        for (var i = 0; i < Headers.Count && i < Rows[row].Count; i++)
        {
            if (string.Equals(Headers[i], header, StringComparison.OrdinalIgnoreCase))
                return Rows[row][i];
        }

        return null;
    }

    public override string ToString()
        => string.Join("\n", new[] { Headers }.Concat(Rows).Select(r => "| " + string.Join(" | ", r) + " |"));

    /// <summary>
    /// A table written as pipe-delimited text, the exact inverse of <see cref="ToString"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how a <b>projected</b> test supplies a table. A C# test has no trailing
    /// <c>|...|</c> block, and the alternatives were worse: calling a row grammar N times renders as
    /// N steps and loses the grid, and a collection-of-tuples argument fixes the columns in the
    /// signature. A table literal keeps the grid and the step as one:
    /// </para>
    /// <code>
    /// TheUsersAre("""
    ///     | first  | last   |
    ///     | LeBron | James  |
    ///     | Chris  | Paul   |
    ///     """);
    /// </code>
    /// <para>
    /// A markdown table pastes in unchanged: the alignment row (<c>|---|:--:|</c>) is recognised and
    /// dropped, leading and trailing pipes are optional, cells are trimmed, and blank lines are
    /// ignored. What is deliberately NOT supported is markdown's escaping and inline formatting — a
    /// cell is the text between pipes.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The text has no rows at all, so there are no headers.</exception>
    public static StepTable Parse(string text)
    {
        var rows = new List<IReadOnlyList<string>>();

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim().Trim('\r');
            if (trimmed.Length == 0) continue;

            var cells = splitRow(trimmed);
            if (cells.Count == 0) continue;

            // A markdown alignment row is punctuation, not data.
            if (rows.Count == 1 && cells.All(isAlignment)) continue;

            rows.Add(cells);
        }

        if (rows.Count == 0)
            throw new ArgumentException(
                "A table literal needs at least a header row, written as pipe-delimited text: " +
                "\"| first | last |\"", nameof(text));

        return new StepTable(rows[0], rows.Skip(1).ToList());
    }

    /// <summary>
    /// Reads a table literal as a table. Lets a call site pass the text itself, which is the whole
    /// point — see <see cref="Parse"/>.
    /// </summary>
    public static implicit operator StepTable(string text) => Parse(text);

    private static List<string> splitRow(string line)
    {
        // Leading and trailing pipes are optional, so a row is the text BETWEEN them.
        var body = line;
        if (body.StartsWith("|", StringComparison.Ordinal)) body = body.Substring(1);
        if (body.EndsWith("|", StringComparison.Ordinal)) body = body.Substring(0, body.Length - 1);

        if (body.Trim().Length == 0 && !line.Contains('|')) return new List<string>();

        return body.Split('|').Select(c => c.Trim()).ToList();
    }

    private static bool isAlignment(string cell)
        => cell.Length > 0 && cell.All(c => c is '-' or ':' or ' ');
}
