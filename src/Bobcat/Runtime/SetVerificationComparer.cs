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
        var run = Cells(actual, expectedRows, keyColumns, ordered, scalarColumn);

        result.IsSetVerification = true;
        result.SetVerificationColumns = run.Columns;
        result.MarkCells(run.Cells.ToArray());

        if (run.Succeeded)
            result.MarkSuccess();
        else
            result.MarkFailed();
    }

    /// <summary>
    /// The comparison itself: the grid an actual collection and a document's expected rows produce,
    /// with no step of any kind in the signature.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split out of <see cref="Compare(IEnumerable,IReadOnlyList{Dictionary{string,string}},string[],StepResult,bool,string?)"/>
    /// so a set verification can be made from somewhere that has no <see cref="StepResult"/> — a
    /// hand-written step with only its context, or a grammar called straight from a C# test. The
    /// cells, the columns and the verdict are the whole of what a set verification produces; who
    /// they are reported to is the caller's business, and <see cref="TableRun.Report"/> is how.
    /// </para>
    /// <para>
    /// Every parameter means exactly what the <c>[SetVerification]</c> attribute's property of the
    /// same name means. See <see cref="Compare(IEnumerable,IReadOnlyList{Dictionary{string,string}},string[],StepResult,bool,string?)"/>
    /// for what <paramref name="ordered"/> and <paramref name="scalarColumn"/> decide.
    /// </para>
    /// </remarks>
    /// <param name="columns">
    /// The column order, when the caller knows it independently of the rows — a table's header row
    /// says what the columns are even when it has no rows under it, and "the set should be empty"
    /// is a real expectation. Null reads them off the first expected row, as the generated path
    /// always has.
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
                cells.Add(new CellResult("missing-row", ResultStatus.missing,
                    $"Expected row not found: {keyDesc}")
                    { RowIndex = rowIndex });

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
            cells.Add(new CellResult("extra-row", ResultStatus.invalid,
                $"Extra row: {desc}")
                { RowIndex = rowIndex });

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
    /// Verify a collection against a <see cref="StepTable"/> and report the grid — the form a
    /// hand-written step uses, and the only form a C# test can reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One grammar body, both lanes.</b> A step declaring a <c>StepTable</c> parameter is handed
    /// the table that trails it in a <c>.feature</c> file, or a table literal from a C# call site, so
    /// a set verification written this way is the same comparison over the same document either way:
    /// </para>
    /// <code>
    /// [Then("the inventory should be")]
    /// public void TheInventoryShouldBe(StepTable expected)
    ///     =&gt; VerifySet(_inventory.Values, expected, keyColumns: "Sku");
    /// </code>
    /// <para>
    /// <b><c>[SetVerification]</c> is still the canonical declarative form</b> and is not replaced by
    /// this. It carries <c>KeyColumns</c>, <c>Ordered</c> and <c>Column</c> as compile-time facts, which
    /// is what lets the preview and the editor see them, and it needs no table-shaped parameter. This
    /// is the escape hatch, at the price of those settings being arguments rather than declarations —
    /// and it is the one a projected test can call, because the expected rows reach it as an argument
    /// instead of from the generator.
    /// </para>
    /// </remarks>
    /// <param name="keyColumns">
    /// The columns that identify a row, comma-separated — <c>[SetVerification(KeyColumns = "…")]</c>
    /// spelled as an argument. Empty matches on every column the table names.
    /// </param>
    /// <param name="column">
    /// For a set of plain values, the single column each value is compared under. Left empty with a
    /// one-column table over scalar elements it is inferred from the header, because there is only
    /// one thing it could be; <c>[SetVerification(Column = "…")]</c> has to be told because
    /// BOBCAT031 is a compile-time diagnostic with no table in view.
    /// </param>
    public static TableRun Verify(
        IEnumerable actual,
        StepTable expected,
        IStepContext? context = null,
        string keyColumns = "",
        bool ordered = false,
        string column = "")
    {
        var rows = expected.AsDictionaries()
            .Select(r => new Dictionary<string, string>(r, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var run = Cells(actual, rows, ParseKeyColumns(keyColumns), ordered,
            scalarColumn(actual, expected, column), expected.Headers);

        run.Report(context);
        return run;
    }

    /// <summary>
    /// A comma-separated key column list, read the way the generator reads
    /// <c>[SetVerification(KeyColumns = "…")]</c> — split on commas, each name trimmed. The
    /// generator materializes that split at compile time into the array it emits; this is the same
    /// reading for a list that arrives as an argument, so the two forms cannot mean different
    /// things over the same string.
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
    /// The runtime reading of the generator's <c>IsSimpleType</c>: a value a single Gherkin cell
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
