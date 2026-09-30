namespace Bobcat;

/// <summary>
/// A step's whole Gherkin data table, handed to a step method as one argument. Declare a
/// parameter of this type (nullable when the table is optional) on a <c>[Given]</c>/<c>[When]</c>/
/// <c>[Then]</c> method and the generator passes the table that trails the step — headers and
/// rows as written — instead of calling the method once per row as <c>[Table]</c> does. The
/// parameter never binds to a column and is never resolved from DI.
/// </summary>
/// <remarks>
/// Built for grammars whose rows have no fixed shape at compile time — event records of several
/// types in one table, a command bound by column name at runtime. A step whose columns are known
/// up front is better served by <c>[Table]</c>, <c>[DecisionTable]</c> or <c>[SetVerification]</c>,
/// which bind and compare at compile time.
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
    /// A table written as pipe-delimited text — the same table a <c>.feature</c> file writes, and
    /// the exact inverse of <see cref="ToString"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how a <b>projected</b> test supplies a table. A C# test has no trailing
    /// <c>|...|</c> block, and the alternatives were worse: calling a row grammar N times renders as
    /// N steps and loses the grid, and a collection-of-tuples argument needs a second grammar
    /// written for the C# lane beside the one the feature file uses. A table literal means
    /// <b>one grammar body serves both lanes</b> — the document supplies the table, or the caller
    /// does:
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
    /// cell is the text between pipes, because that is what a Gherkin cell is, and two rules for
    /// reading a cell is how the two lanes would drift.
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
