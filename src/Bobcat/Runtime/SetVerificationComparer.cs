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
    /// Compare an actual collection against expected rows, producing per-cell CellResults.
    /// </summary>
    public static void Compare(
        IEnumerable actual,
        IReadOnlyList<Dictionary<string, string>> expectedRows,
        string[] keyColumns,
        StepResult result)
    {
        var actualRows = ToRows(actual);
        var matchedActualIndices = new HashSet<int>();
        var cells = new List<CellResult>();
        var hasFailure = false;
        var rowIndex = 0;

        var columns = expectedRows.Count > 0
            ? expectedRows[0].Keys.ToList()
            : new List<string>();

        foreach (var expected in expectedRows)
        {
            var matchIndex = FindMatch(expected, actualRows, keyColumns, matchedActualIndices);

            if (matchIndex >= 0)
            {
                matchedActualIndices.Add(matchIndex);
                var actualRow = actualRows[matchIndex];

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
                hasFailure = true;
            }

            rowIndex++;
        }

        for (var i = 0; i < actualRows.Count; i++)
        {
            if (matchedActualIndices.Contains(i)) continue;
            var extra = actualRows[i];
            var desc = string.Join(", ", extra.Select(kv => $"{kv.Key}={Format(kv.Value)}"));
            cells.Add(new CellResult("extra-row", ResultStatus.invalid,
                $"Extra row: {desc}")
                { RowIndex = rowIndex++ });
        }

        result.IsSetVerification = true;
        result.SetVerificationColumns = columns;
        result.MarkCells(cells.ToArray());

        if (hasFailure)
            result.MarkFailed();
        else
            result.MarkSuccess();
    }

    private static int FindMatch(
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

    private static List<Dictionary<string, object?>> ToRows(IEnumerable actual)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var item in actual)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                row[prop.Name] = prop.GetValue(item);
            }
            rows.Add(row);
        }
        return rows;
    }

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}
