using System.Collections;
using System.Reflection;
using Bobcat.Engine;

namespace Bobcat.Runtime;

/// <summary>
/// Static utility for set verification comparison.
/// Called by source-generated code with pre-generated expected data.
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
                    var actualVal = actualRow.GetValueOrDefault(col, "");

                    if (string.Equals(expectedVal, actualVal, StringComparison.Ordinal))
                    {
                        cells.Add(new CellResult(col, ResultStatus.success, expectedVal)
                            { RowIndex = rowIndex });
                    }
                    else
                    {
                        cells.Add(new CellResult(col, ResultStatus.failed,
                            $"expected '{expectedVal}', got '{actualVal}'")
                            { RowIndex = rowIndex });
                        hasFailure = true;
                    }
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
            var desc = string.Join(", ", extra.Select(kv => $"{kv.Key}={kv.Value}"));
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
        List<Dictionary<string, string>> actuals,
        string[] keyColumns,
        HashSet<int> alreadyMatched)
    {
        for (var i = 0; i < actuals.Count; i++)
        {
            if (alreadyMatched.Contains(i)) continue;
            var actual = actuals[i];

            if (keyColumns.Length > 0)
            {
                var allKeysMatch = keyColumns.All(key =>
                    expected.TryGetValue(key, out var ev) &&
                    actual.TryGetValue(key, out var av) &&
                    string.Equals(ev, av, StringComparison.Ordinal));
                if (allKeysMatch) return i;
            }
            else
            {
                var allMatch = expected.All(kv =>
                    actual.TryGetValue(kv.Key, out var av) &&
                    string.Equals(kv.Value, av, StringComparison.Ordinal));
                if (allMatch) return i;
            }
        }
        return -1;
    }

    private static List<Dictionary<string, string>> ToRows(IEnumerable actual)
    {
        var rows = new List<Dictionary<string, string>>();
        foreach (var item in actual)
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                row[prop.Name] = prop.GetValue(item)?.ToString() ?? "";
            }
            rows.Add(row);
        }
        return rows;
    }
}
