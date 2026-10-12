using System.Collections;
using System.Reflection;
using Bobcat.Engine;
using Bobcat.Engine.Verification;

namespace Bobcat.Runtime;

/// <summary>
/// Static utility for set verification comparison.
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
    /// The comparison itself: the grid an actual collection and a document's expected rows produce,
    /// with no step of any kind in the signature.
    /// </summary>
    /// <param name="ordered">
    /// When true the expected rows must also appear in the order the specification writes them:
    /// rows are still matched by <paramref name="keyColumns"/>, and a matched row that turns up
    /// behind one written before it is reported as out of order. See the remarks.
    /// </param>
    /// <param name="scalarColumn">
    /// For a collection of plain values, the single column each value is compared under. Null for a
    /// collection of objects, whose columns are read off its properties.
    /// </param>
    /// <remarks>
    /// <para>
    /// The cells, the columns and the verdict are the whole of what a set verification produces; who
    /// they are reported to is the caller's business, and <see cref="TableRun.Report"/> is how.
    /// </para>
    /// <para>
    /// <b>Order is checked after matching, not instead of it.</b> Comparing position by position
    /// would read one inserted row as every row after it disagreeing — an event stream with a
    /// single extra event would report every later event wrong. Matching first means an insertion
    /// is one extra row, a deletion is one missing row, and a genuine reordering is the only thing
    /// reported as a reordering.
    /// </para>
    /// <para>
    /// So key columns earn their keep in an ordered comparison too: with no key columns a row
    /// is matched on every column, and a row with one wrong value is then a missing row beside an
    /// extra one rather than a row with one wrong cell. Naming the columns that identify a row —
    /// the event type, the SKU — is what turns that back into a cell-level disagreement.
    /// </para>
    /// </remarks>
    /// <param name="columns">
    /// The column order, when the caller knows it independently of the rows — a table's header row
    /// says what the columns are even when it has no rows under it, and "the set should be empty"
    /// is a real expectation. Null reads them off the first expected row.
    /// </param>
    public static TableRun Cells(
        IEnumerable actual,
        IReadOnlyList<Dictionary<string, string>> expectedRows,
        string[] keyColumns,
        bool ordered = false,
        string? scalarColumn = null,
        IReadOnlyList<string>? columns = null)
    {
        var actualRows = toRows(actual, scalarColumn);
        var matchedActualIndices = new HashSet<int>();
        var cells = new List<CellResult>();
        var rowIndex = 0;

        // The furthest position any earlier expected row was found at. An ordered comparison fails
        // the first row that turns up behind it.
        var furthestMatched = -1;

        var columnList = columns?.ToList()
                         ?? (expectedRows.Count > 0 ? expectedRows[0].Keys.ToList() : new List<string>());

        var pairs = ordered
            ? pairInOrder(expectedRows, actualRows, keyColumns)
            : null;

        foreach (var expected in expectedRows)
        {
            var matchIndex = pairs?[rowIndex] ?? findMatch(expected, actualRows, keyColumns, matchedActualIndices);

            if (matchIndex >= 0)
            {
                matchedActualIndices.Add(matchIndex);
                var actualRow = actualRows[matchIndex];

                if (ordered && matchIndex < furthestMatched)
                {
                    // Note, not the legacy display text (issue #396): this cell's text is a
                    // sentence rather than a value, and Note is the field the wire carries it in.
                    cells.Add(new CellResult(OutOfOrderCell, ResultStatus.failed)
                    {
                        Note = $"Out of order: found at position {matchIndex + 1}; a row written "
                               + $"earlier is at position {furthestMatched + 1}",
                        RowIndex = rowIndex
                    });
                }

                furthestMatched = Math.Max(furthestMatched, matchIndex);

                foreach (var col in expected.Keys)
                {
                    var expectedVal = expected[col];
                    var actualVal = actualRow.GetValueOrDefault(col);

                    cells.Add(CellCheck.ForValue(col, actualVal, expectedVal, CheckOptions.Default, rowIndex));
                }
            }
            else
            {
                var keyDesc = string.Join(", ", expected.Select(kv => $"{kv.Key}={kv.Value}"));
                cells.Add(new CellResult("missing-row", ResultStatus.missing)
                {
                    Note = $"Expected row not found: {keyDesc}",
                    RowIndex = rowIndex
                });

                // The row's expected values, one cell per column, so a renderer can show
                // WHICH row was missing in place instead of a row of dashes. Status is `ok`
                // deliberately: the missing-row cell above is the one failure this row counts as.
                foreach (var col in columnList)
                {
                    cells.Add(new CellResult(col, ResultStatus.ok)
                    {
                        Expected = expected.GetValueOrDefault(col, ""),
                        RowIndex = rowIndex
                    });
                }
            }

            rowIndex++;
        }

        for (var i = 0; i < actualRows.Count; i++)
        {
            if (matchedActualIndices.Contains(i)) continue;
            var extra = actualRows[i];
            var desc = string.Join(", ", extra.Select(kv => $"{kv.Key}={format(kv.Value)}"));

            // `invalid`, not `ok`, because an extra row fails the step exactly as a missing one
            // does. A set verification says the set is exactly this; a row the specification does
            // not describe is a disagreement, and the run already counted it as an error — only the
            // step's own verdict once said otherwise, which read as a green step under a red
            // scenario.
            cells.Add(new CellResult("extra-row", ResultStatus.invalid)
            {
                Note = $"Extra row: {desc}",
                RowIndex = rowIndex
            });

            // The same treatment as a missing row: the actual values per column, uncounted,
            // so the grid shows the row rather than the renderer re-parsing the description.
            foreach (var col in columnList)
            {
                cells.Add(new CellResult(col, ResultStatus.ok)
                {
                    Actual = extra.TryGetValue(col, out var value) ? format(value) : "",
                    RowIndex = rowIndex
                });
            }

            rowIndex++;
        }

        var run = new TableRun(columnList);
        run.Cells.AddRange(cells);
        return run;
    }

    /// <summary>
    /// Verify a collection against a <see cref="StepTable"/> and report the grid on the recorded
    /// step, if one is open.
    /// </summary>
    /// <example>
    /// <code>
    /// [Then("the inventory should be")]
    /// public void TheInventoryShouldBe(StepTable expected)
    ///     =&gt; SetVerificationComparer.Verify(_inventory.Values, expected, keyColumns: "Sku");
    /// </code>
    /// </example>
    /// <param name="keyColumns">
    /// The columns that identify a row, comma-separated. Empty matches on every column the table
    /// names.
    /// </param>
    /// <param name="ordered">Whether the rows must also appear in the order the table writes them.</param>
    /// <param name="column">
    /// For a set of plain values, the single column each value is compared under. Left empty with a
    /// one-column table over scalar elements it is inferred from the header, because there is only
    /// one thing it could be.
    /// </param>
    public static TableRun Verify(
        IEnumerable actual,
        StepTable expected,
        string keyColumns = "",
        bool ordered = false,
        string column = "")
    {
        var rows = expected.AsDictionaries()
            .Select(r => new Dictionary<string, string>(r, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var run = Cells(actual, rows, ParseKeyColumns(keyColumns), ordered,
            scalarColumn(actual, expected, column), expected.Headers);

        run.Report();
        return run;
    }

    /// <summary>
    /// A comma-separated key column list — split on commas, each name trimmed. The one authority on
    /// that reading.
    /// </summary>
    public static string[] ParseKeyColumns(string keyColumns)
        => string.IsNullOrWhiteSpace(keyColumns)
            ? []
            : keyColumns.Split(',').Select(k => k.Trim()).Where(k => k.Length > 0).ToArray();

    /// <summary>
    /// The one column a set of plain values is compared under: what the caller named, else the
    /// table's single header when the elements are scalar, else null for a set of objects.
    /// </summary>
    private static string? scalarColumn(IEnumerable actual, StepTable expected, string column)
    {
        if (column.Length > 0) return column;

        var first = actual.Cast<object?>().FirstOrDefault(v => v is not null);
        if (first is null || !isScalar(first.GetType())) return null;

        if (expected.Headers.Count == 1) return expected.Headers[0];

        throw new SpecCriticalException(
            $"This step verifies a set of {first.GetType().Name} values, which compare under one " +
            $"column, but its table has {expected.Headers.Count}: " +
            $"{string.Join(", ", expected.Headers)}. Name the column the values belong in, or give " +
            "the table one column.");
    }

    /// <summary>
    /// The runtime reading of the generator's <c>IsSimpleType</c>: a value a single cell
    /// stands for on its own, rather than an object whose properties are the row.
    /// </summary>
    private static bool isScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        return type.IsPrimitive
               || type.IsEnum
               || type == typeof(string)
               || type == typeof(decimal)
               || type == typeof(Guid)
               || type == typeof(DateTime)
               || type == typeof(DateTimeOffset)
               || type == typeof(DateOnly)
               || type == typeof(TimeOnly)
               || type == typeof(TimeSpan);
    }

    private static int findMatch(
        Dictionary<string, string> expected,
        List<Dictionary<string, object?>> actuals,
        string[] keyColumns,
        HashSet<int> alreadyMatched)
    {
        for (var i = 0; i < actuals.Count; i++)
        {
            if (alreadyMatched.Contains(i)) continue;
            if (matches(expected, actuals[i], keyColumns)) return i;
        }

        return -1;
    }

    private static bool matches(Dictionary<string, string> expected, Dictionary<string, object?> actual, string[] keyColumns)
    {
        var matchColumns = keyColumns.Length > 0 ? keyColumns : expected.Keys.ToArray();

        return matchColumns.All(key =>
            expected.TryGetValue(key, out var expectedVal) &&
            CellCheck.ForValue(key, actual.GetValueOrDefault(key), expectedVal).Status == ResultStatus.success);
    }

    /// <summary>
    /// The pairing for an ordered comparison (bobcat#476): the longest one that keeps both sides in
    /// order first — a longest common subsequence over the rows that match — so a row is only reported
    /// out of order when no pairing avoids it. First come first served could hand a row an actual one
    /// that sits behind a row written earlier while a later actual row that keeps the order went spare.
    /// The rows left over then take the first unpaired actual row they match, preferring one that sits
    /// between their neighbours' positions. -1 is a missing row.
    /// </summary>
    private static int[] pairInOrder(
        IReadOnlyList<Dictionary<string, string>> expectedRows,
        List<Dictionary<string, object?>> actualRows,
        string[] keyColumns)
    {
        var expectedCount = expectedRows.Count;
        var actualCount = actualRows.Count;
        var match = new bool[expectedCount, actualCount];
        for (var i = 0; i < expectedCount; i++)
        {
            for (var j = 0; j < actualCount; j++)
            {
                match[i, j] = matches(expectedRows[i], actualRows[j], keyColumns);
            }
        }

        var longest = new int[expectedCount + 1, actualCount + 1];
        for (var i = expectedCount - 1; i >= 0; i--)
        {
            for (var j = actualCount - 1; j >= 0; j--)
            {
                var skip = Math.Max(longest[i + 1, j], longest[i, j + 1]);
                longest[i, j] = match[i, j] ? Math.Max(1 + longest[i + 1, j + 1], skip) : skip;
            }
        }

        var pairs = new int[expectedCount];
        Array.Fill(pairs, -1);

        // The walk back takes a diagonal whenever it is as long as skipping, so expected[i] keeps
        // actual[i] whenever the two agree position by position
        int x = 0, y = 0;
        while (x < expectedCount && y < actualCount)
        {
            if (match[x, y] && longest[x, y] == 1 + longest[x + 1, y + 1])
            {
                pairs[x++] = y++;
            }
            else if (longest[x + 1, y] >= longest[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        var taken = new HashSet<int>(pairs.Where(p => p >= 0));
        for (var i = 0; i < expectedCount; i++)
        {
            if (pairs[i] >= 0) continue;

            var after = -1;
            for (var k = i - 1; k >= 0; k--)
            {
                if (pairs[k] < 0) continue;
                after = pairs[k];
                break;
            }

            var before = actualCount;
            for (var k = i + 1; k < expectedCount; k++)
            {
                if (pairs[k] < 0) continue;
                before = pairs[k];
                break;
            }

            var candidate = -1;
            for (var j = 0; j < actualCount; j++)
            {
                if (taken.Contains(j) || !match[i, j]) continue;
                if (j > after && j < before)
                {
                    candidate = j;
                    break;
                }

                if (candidate < 0) candidate = j;
            }

            if (candidate < 0) continue;
            pairs[i] = candidate;
            taken.Add(candidate);
        }

        return pairs;
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
                    // [Header] on the property titles the column it is compared under — Storyteller's
                    // _.Compare(o => o.Amount).Header("The Amount"). The title REPLACES the name, so a
                    // document writes the title; see HeaderAttribute for why it is not an alias.
                    row[ColumnNames.Of(prop)] = prop.GetValue(item);
                }
            }

            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// An actual value as text, through the same formatter the per-cell comparison uses — so an
    /// extra row's values and a compared cell's actual value read the same way in one grid. Its own
    /// copy rendered a <c>DateOnly</c> as the invariant culture's "10/01/2026" beside a compared
    /// cell's ISO "2026-09-26", which is the same value printed two ways in one table.
    /// </summary>
    private static string format(object? value) => CheckFormat.Of(value);
}
