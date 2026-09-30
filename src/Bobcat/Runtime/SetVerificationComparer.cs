using System.Collections;
using System.Reflection;
using Bobcat.Engine;
using Bobcat.Engine.Verification;

namespace Bobcat.Runtime;

/// <summary>
/// Static utility for set verification comparison.
/// Called by source-generated code with pre-generated expected data.
/// Per-cell comparisons go through <see cref="CellCheck"/> so they are type-aware
/// (driven by the runtime type of each actual property value) and produce
/// structured Expected/Actual/Note on every <see cref="CellResult"/>.
/// </summary>
public static class SetVerificationComparer
{
    /// <summary>
    /// The cell name that carries a row matched in the wrong place, in an ordered comparison.
    /// Sibling of <c>missing-row</c>, <c>extra-row</c> and
    /// <see cref="DecisionTableComparer.RowErrorCell"/>.
    /// </summary>
    public const string OutOfOrderCell = "out-of-order";

    /// <summary>
    /// Compare an actual collection against expected rows, producing per-cell CellResults.
    /// </summary>
    /// <param name="ordered">
    /// When true the expected rows must also appear in the order the specification writes them:
    /// rows are still matched by <paramref name="keyColumns"/>, and a matched row that turns up
    /// behind one written before it is reported as out of order. See the remarks.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Order is checked after matching, not instead of it.</b> Comparing position by position
    /// would read one inserted row as every row after it disagreeing — an event stream with a
    /// single extra event would report every later event wrong. Matching first means an insertion
    /// is one extra row, a deletion is one missing row, and a genuine reordering is the only thing
    /// reported as a reordering.
    /// </para>
    /// <para>
    /// So <c>KeyColumns</c> earns its keep in an ordered comparison too: with no key columns a row
    /// is matched on every column, and a row with one wrong value is then a missing row beside an
    /// extra one rather than a row with one wrong cell. Naming the columns that identify a row —
    /// the event type, the SKU — is what turns that back into a cell-level disagreement.
    /// </para>
    /// </remarks>
    /// <param name="scalarColumn">
    /// For a collection of plain values, the single column each value is compared under —
    /// <c>[SetVerification(Column = "…")]</c>. Null for a collection of objects, whose columns are
    /// read off its properties.
    /// </param>
    public static void Compare(
        IEnumerable actual,
        IReadOnlyList<Dictionary<string, string>> expectedRows,
        string[] keyColumns,
        StepResult result,
        bool ordered = false,
        string? scalarColumn = null)
    {
        var actualRows = toRows(actual, scalarColumn);
        var matchedActualIndices = new HashSet<int>();
        var cells = new List<CellResult>();
        var hasFailure = false;
        var rowIndex = 0;

        // The furthest position any earlier expected row was found at. An ordered comparison fails
        // the first row that turns up behind it.
        var furthestMatched = -1;

        var columns = expectedRows.Count > 0
            ? expectedRows[0].Keys.ToList()
            : new List<string>();

        foreach (var expected in expectedRows)
        {
            var matchIndex = findMatch(expected, actualRows, keyColumns, matchedActualIndices);

            if (matchIndex >= 0)
            {
                matchedActualIndices.Add(matchIndex);
                var actualRow = actualRows[matchIndex];

                if (ordered && matchIndex < furthestMatched)
                {
                    cells.Add(new CellResult(OutOfOrderCell, ResultStatus.failed,
                            $"Out of order: found at position {matchIndex + 1}; a row written " +
                            $"earlier is at position {furthestMatched + 1}")
                        { RowIndex = rowIndex });
                    hasFailure = true;
                }

                furthestMatched = Math.Max(furthestMatched, matchIndex);

                foreach (var col in expected.Keys)
                {
                    var expectedVal = expected[col];
                    var actualVal = actualRow.GetValueOrDefault(col);

                    var cell = CellCheck.ForValue(col, actualVal, expectedVal, CheckOptions.Default, rowIndex);
                    cells.Add(cell);
                    if (cell.Status != ResultStatus.success)
                        hasFailure = true;
                }
            }
            else
            {
                var keyDesc = string.Join(", ", expected.Select(kv => $"{kv.Key}={kv.Value}"));
                cells.Add(new CellResult("missing-row", ResultStatus.missing,
                    $"Expected row not found: {keyDesc}")
                    { RowIndex = rowIndex });

                // The row's expected values, one cell per column, so a renderer can show
                // WHICH row was missing in place instead of a row of dashes. Status is `ok`
                // deliberately: the missing-row cell above is the one failure this row counts as.
                foreach (var col in columns)
                {
                    cells.Add(new CellResult(col, ResultStatus.ok)
                    {
                        Expected = expected.GetValueOrDefault(col, ""),
                        RowIndex = rowIndex
                    });
                }

                hasFailure = true;
            }

            rowIndex++;
        }

        for (var i = 0; i < actualRows.Count; i++)
        {
            if (matchedActualIndices.Contains(i)) continue;
            var extra = actualRows[i];
            var desc = string.Join(", ", extra.Select(kv => $"{kv.Key}={format(kv.Value)}"));
            cells.Add(new CellResult("extra-row", ResultStatus.invalid,
                $"Extra row: {desc}")
                { RowIndex = rowIndex });

            // The same treatment as a missing row: the actual values per column, uncounted,
            // so the grid shows the row rather than the renderer re-parsing the description.
            foreach (var col in columns)
            {
                cells.Add(new CellResult(col, ResultStatus.ok)
                {
                    Actual = extra.TryGetValue(col, out var value) ? format(value) : "",
                    RowIndex = rowIndex
                });
            }

            // An extra row fails the step, like a missing one. A set verification says the set is
            // exactly this; a row the specification does not describe is a disagreement, and the
            // run already counted it as an error — only the step's own verdict said otherwise,
            // which read as a green step under a red scenario.
            hasFailure = true;

            rowIndex++;
        }

        result.IsSetVerification = true;
        result.SetVerificationColumns = columns;
        result.MarkCells(cells.ToArray());

        if (hasFailure)
            result.MarkFailed();
        else
            result.MarkSuccess();
    }

    private static int findMatch(
        Dictionary<string, string> expected,
        List<Dictionary<string, object?>> actuals,
        string[] keyColumns,
        HashSet<int> alreadyMatched)
    {
        var matchColumns = keyColumns.Length > 0 ? keyColumns : expected.Keys.ToArray();

        for (var i = 0; i < actuals.Count; i++)
        {
            if (alreadyMatched.Contains(i)) continue;
            var actual = actuals[i];

            var allMatch = matchColumns.All(key =>
                expected.TryGetValue(key, out var expectedVal) &&
                CellCheck.ForValue(key, actual.GetValueOrDefault(key), expectedVal).Status == ResultStatus.success);

            if (allMatch) return i;
        }

        return -1;
    }

    private static List<Dictionary<string, object?>> toRows(IEnumerable actual, string? scalarColumn)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var item in actual)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (scalarColumn != null)
            {
                // A set of values: the item IS the row, under the one column the fixture named.
                // Reading properties off it instead would compare a string against Length and
                // Chars, which is what made every row read as missing and extra at once.
                row[scalarColumn] = item;
            }
            else
            {
                foreach (var prop in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    row[prop.Name] = prop.GetValue(item);
                }
            }

            rows.Add(row);
        }
        return rows;
    }

    private static string format(object? value) => value switch
    {
        null => "NULL",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}
