using Bobcat.Engine;

namespace Bobcat.Runtime;

/// <summary>One leaf of an expected object that the actual one disagreed on.</summary>
public sealed record ValueDifference(string Path, object? Expected, object? Actual);

/// <summary>
/// Set verification over OBJECTS rather than a document's table — the events a command appended,
/// the messages it sent — with Storyteller's reading of the result: each expected item is matched,
/// near-matched, or MISSING; each actual item nobody expected is EXTRA; and in an ordered comparison
/// a matched item in the wrong place is ORDER.
/// </summary>
/// <remarks>
/// <para>
/// <b>Matched first, then compared.</b> The same rule as <see cref="SetVerificationComparer"/>, for
/// the same reason: comparing position by position reads one missing event as every later event
/// wrong. An exact match is looked for first; an expected item with none is then paired with an
/// unmatched actual item of the same TYPE, and that row is a FAIL with the leaves that disagreed,
/// which is what "the right event with the wrong amount" deserves rather than a missing row beside an
/// extra one. Only what is left is missing or extra.
/// </para>
/// <para>
/// <b>The comparison is the caller's.</b> A structural comparer with its own rules — members to
/// ignore, a tolerance — is handed in, so this decides how a set is matched and rendered and never
/// what makes two objects equal.
/// </para>
/// <para>
/// The grid has two columns, the item's type under <paramref name="noun"/> and its values on one line
/// through <see cref="ScenarioValues.DescribeProperties"/>, so a heterogeneous stream — five event types with
/// five shapes — is still one table.
/// </para>
/// </remarks>
public static class ObjectSetVerification
{
    public const string ValuesColumn = "values";

    /// <summary>The cell an <see cref="Absent"/> check adds to a row whose forbidden item was found.</summary>
    public const string PresentCell = "present-row";

    /// <param name="actual">What the system produced, in the order it produced it.</param>
    /// <param name="expected">What the specification expects, as plain values (unwrapped).</param>
    /// <param name="compare">
    /// The leaves where <c>actual</c> disagrees with <c>expected[index]</c>; empty means equal.
    /// Only ever called with an actual item of the same runtime type as the expected one.
    /// </param>
    /// <param name="noun">The first column's heading — "event", "message".</param>
    /// <param name="ordered">Whether position matters. See the remarks.</param>
    public static TableRun Cells(
        IReadOnlyList<object> actual,
        IReadOnlyList<object> expected,
        Func<object, int, IReadOnlyList<ValueDifference>> compare,
        string noun,
        bool ordered = true)
        => Cells(actual, expected, compare, noun, ordered ? SetMode.Ordered : SetMode.AnyOrder);

    /// <summary>
    /// Verify <paramref name="actual"/> against <paramref name="expected"/>, where each expected item is
    /// a whole object, an <see cref="IExpectedValue"/>, or a partial object judged only on the members it
    /// names (bobcat#418).
    /// </summary>
    public static TableRun Verify(IReadOnlyList<object> actual, IReadOnlyList<object> expected, string noun,
        SetMode mode = SetMode.Ordered)
        => Cells(actual, expected, (item, i) => PartialMatching.Differences(item, expected[i]), noun, mode);

    /// <summary>
    /// The general form: <paramref name="expected"/> items may be whole objects, <see cref="IExpectedValue"/>s
    /// or partial objects; <paramref name="mode"/> says whether order matters and whether items nobody
    /// expected are allowed.
    /// </summary>
    /// <remarks>
    /// <b>Pairing is a maximum matching, not first come first served.</b> With partial objects one actual
    /// item can satisfy two expectations — two <c>ShipmentConfirmed</c>s where one expectation names only
    /// the carrier — and pairing greedily would then report a MISSING row that a different pairing
    /// avoids. Exact pairs are chosen by augmenting paths (Kuhn's algorithm), trying actual items in the
    /// order they happened, so the result never depends on the order the expectations were written in.
    /// </remarks>
    public static TableRun Cells(
        IReadOnlyList<object> actual,
        IReadOnlyList<object> expected,
        Func<object, int, IReadOnlyList<ValueDifference>> compare,
        string noun,
        SetMode mode)
    {
        var exact = new bool[expected.Count, actual.Count];
        for (var i = 0; i < expected.Count; i++)
        {
            var type = PartialMatching.ExpectedType(expected[i]);
            for (var j = 0; j < actual.Count; j++)
            {
                exact[i, j] = actual[j].GetType() == type && compare(actual[j], i).Count == 0;
            }
        }

        var pairs = new int[expected.Count];
        Array.Fill(pairs, -1);
        var owner = new int[actual.Count];
        Array.Fill(owner, -1);

        // Pass 1: exact matches, as many as any pairing can make
        for (var i = 0; i < expected.Count; i++)
        {
            augment(i, exact, pairs, owner, new bool[actual.Count]);
        }

        var differences = new IReadOnlyList<ValueDifference>?[expected.Count];

        // Pass 2: the same type with different values — a FAIL row, not MISSING beside EXTRA
        for (var i = 0; i < expected.Count; i++)
        {
            if (pairs[i] >= 0) continue;
            var type = PartialMatching.ExpectedType(expected[i]);
            for (var j = 0; j < actual.Count; j++)
            {
                if (owner[j] >= 0 || actual[j].GetType() != type) continue;

                pairs[i] = j;
                owner[j] = i;
                differences[i] = compare(actual[j], i);
                break;
            }
        }

        var columns = new List<string> { noun, ValuesColumn };
        var run = new TableRun(columns);
        var row = 0;
        var furthest = -1;

        for (var i = 0; i < expected.Count; i++, row++)
        {
            var item = expected[i];
            var typeName = PartialMatching.ExpectedType(item).Name;

            if (pairs[i] < 0)
            {
                run.Cells.Add(new CellResult("missing-row", ResultStatus.missing)
                {
                    Note = $"Expected {typeName} was not found",
                    RowIndex = row
                });
                run.Cells.Add(new CellResult(noun, ResultStatus.ok) { Expected = typeName, RowIndex = row });
                run.Cells.Add(new CellResult(ValuesColumn, ResultStatus.ok) { Expected = PartialMatching.DescribeExpected(item), RowIndex = row });
                continue;
            }

            var position = pairs[i];
            if (mode == SetMode.Ordered && position < furthest)
            {
                run.Cells.Add(new CellResult(SetVerificationComparer.OutOfOrderCell, ResultStatus.failed)
                {
                    Note = $"Out of order: {typeName} was appended at position {position + 1}, "
                           + $"before one the specification writes ahead of it (position {furthest + 1})",
                    RowIndex = row
                });
            }

            furthest = Math.Max(furthest, position);

            run.Cells.Add(new CellResult(noun, ResultStatus.success)
            {
                Expected = typeName,
                Actual = actual[position].GetType().Name,
                RowIndex = row
            });

            if (differences[i] is { Count: > 0 } diffs)
            {
                run.Cells.Add(new CellResult(ValuesColumn, ResultStatus.failed)
                {
                    Expected = string.Join(", ", diffs.Select(d => $"{d.Path}: {ScenarioValues.Format(d.Expected)}")),
                    Actual = string.Join(", ", diffs.Select(d => $"{d.Path}: {ScenarioValues.Format(d.Actual)}")),
                    RowIndex = row
                });
            }
            else
            {
                // Shows what HAPPENED. The two agree on everything the comparison judged, but a member
                // the caller chose to ignore — a minted timestamp — only has a real value on this side.
                // For a partial expectation it is only the members the expectation names.
                var happened = PartialMatching.DescribeActual(actual[position], item);
                run.Cells.Add(new CellResult(ValuesColumn, ResultStatus.success, happened)
                {
                    Expected = PartialMatching.DescribeExpected(item),
                    Actual = happened,
                    RowIndex = row
                });
            }
        }

        // A Contains check allows what nobody expected, so it is neither judged nor shown
        if (mode == SetMode.Contains) return run;

        for (var j = 0; j < actual.Count; j++)
        {
            if (owner[j] >= 0) continue;

            run.Cells.Add(new CellResult("extra-row", ResultStatus.invalid)
            {
                Note = $"Unexpected {actual[j].GetType().Name} at position {j + 1}",
                RowIndex = row
            });
            run.Cells.Add(new CellResult(noun, ResultStatus.ok) { Actual = actual[j].GetType().Name, RowIndex = row });
            run.Cells.Add(new CellResult(ValuesColumn, ResultStatus.ok) { Actual = ScenarioValues.DescribeProperties(actual[j]), RowIndex = row });
            row++;
        }

        return run;
    }

    /// <summary>
    /// Nothing in <paramref name="actual"/> matches any of <paramref name="forbidden"/> — each a
    /// <see cref="Type"/> (no item of that type at all), a partial object (none that agrees on the
    /// members it names), or a whole object. One row per forbidden item: <c>success</c> when absent,
    /// failed with what was found when present.
    /// </summary>
    public static TableRun Absent(IReadOnlyList<object> actual, IReadOnlyList<object> forbidden, string noun)
    {
        var run = new TableRun([noun, ValuesColumn]);
        for (var i = 0; i < forbidden.Count; i++)
        {
            var item = forbidden[i];
            var type = PartialMatching.ExpectedType(item);
            var found = actual.FirstOrDefault(a => item is Type ? a.GetType() == type : PartialMatching.Matches(a, item));
            var expected = PartialMatching.DescribeExpected(item);

            if (found is null)
            {
                run.Cells.Add(new CellResult(noun, ResultStatus.success, type.Name) { Expected = type.Name, RowIndex = i });
                run.Cells.Add(new CellResult(ValuesColumn, ResultStatus.success, expected) { Expected = expected, RowIndex = i });
                continue;
            }

            var position = actual.ToList().IndexOf(found);
            run.Cells.Add(new CellResult(PresentCell, ResultStatus.failed)
            {
                Note = $"{type.Name} was not expected, but one was appended at position {position + 1}",
                RowIndex = i
            });
            run.Cells.Add(new CellResult(noun, ResultStatus.failed) { Expected = type.Name, Actual = type.Name, RowIndex = i });
            run.Cells.Add(new CellResult(ValuesColumn, ResultStatus.failed)
            {
                Expected = expected,
                Actual = item is Type ? ScenarioValues.DescribeProperties(found) : PartialMatching.DescribeActual(found, item),
                RowIndex = i
            });
        }

        return run;
    }

    // Kuhn's augmenting path: give expectation i an actual item, moving an earlier pairing to another
    // item it also matches when that frees one up.
    private static bool augment(int i, bool[,] exact, int[] pairs, int[] owner, bool[] visited)
    {
        for (var j = 0; j < owner.Length; j++)
        {
            if (!exact[i, j] || visited[j]) continue;
            visited[j] = true;

            if (owner[j] < 0 || augment(owner[j], exact, pairs, owner, visited))
            {
                pairs[i] = j;
                owner[j] = i;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The disagreements in <paramref name="run"/> as sentences, one per row — the message a test
    /// throws when no scenario is recording to show the grid.
    /// </summary>
    public static IReadOnlyList<string> Problems(TableRun run, string noun)
    {
        var problems = new List<string>();
        foreach (var group in run.Cells.GroupBy(x => x.RowIndex).OrderBy(x => x.Key))
        {
            var cells = group.ToList();
            string? text(string column, bool expected)
                => cells.FirstOrDefault(c => c.Name == column) is { } c ? expected ? c.Expected : c.Actual : null;

            if (cells.FirstOrDefault(c => c.Name == PresentCell) is { } present)
                problems.Add($"PRESENT {text(noun, false)}({text(ValuesColumn, false)}): {present.Note}");
            else if (cells.Any(c => c.Name == "missing-row"))
                problems.Add($"MISSING {text(noun, true)}({text(ValuesColumn, true)})");
            else if (cells.Any(c => c.Name == "extra-row"))
                problems.Add($"EXTRA {text(noun, false)}({text(ValuesColumn, false)})");
            else if (cells.FirstOrDefault(c => c.Name == SetVerificationComparer.OutOfOrderCell) is { } order)
                problems.Add($"ORDER {order.Note}");
            else if (cells.FirstOrDefault(c => c.Name == ValuesColumn && c.Status == ResultStatus.failed) is { } fail)
                problems.Add($"FAIL {text(noun, true)}: expected {fail.Expected}, was {fail.Actual}");
        }

        return problems;
    }
}

/// <summary>How a set of objects is matched against its expectations.</summary>
public enum SetMode
{
    /// <summary>Exactly these, in this order: unexpected items are EXTRA, misplaced ones ORDER.</summary>
    Ordered,

    /// <summary>Exactly these, in any order: unexpected items are EXTRA.</summary>
    AnyOrder,

    /// <summary>These, in any order, among whatever else there is: nothing is EXTRA.</summary>
    Contains
}
