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
}
