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
/// through <see cref="ScenarioValues.Describe"/>, so a heterogeneous stream — five event types with
/// five shapes — is still one table.
/// </para>
/// </remarks>
public static class ObjectSetVerification
{
    public const string ValuesColumn = "values";

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
    {
        var pairs = new int[expected.Count];
        Array.Fill(pairs, -1);
        var differences = new IReadOnlyList<ValueDifference>?[expected.Count];
        var used = new HashSet<int>();

        // Pass 1: exact matches, earliest first
        for (var i = 0; i < expected.Count; i++)
        {
            for (var j = 0; j < actual.Count; j++)
            {
                if (used.Contains(j) || actual[j].GetType() != expected[i].GetType()) continue;
                if (compare(actual[j], i).Count > 0) continue;

                pairs[i] = j;
                used.Add(j);
                break;
            }
        }

        // Pass 2: the same type with different values — a FAIL row, not MISSING beside EXTRA
        for (var i = 0; i < expected.Count; i++)
        {
            if (pairs[i] >= 0) continue;
            for (var j = 0; j < actual.Count; j++)
            {
                if (used.Contains(j) || actual[j].GetType() != expected[i].GetType()) continue;

                pairs[i] = j;
                differences[i] = compare(actual[j], i);
                used.Add(j);
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

            if (pairs[i] < 0)
            {
                run.Cells.Add(new CellResult("missing-row", ResultStatus.missing)
                {
                    Note = $"Expected {item.GetType().Name} was not found",
                    RowIndex = row
                });
                run.Cells.Add(new CellResult(noun, ResultStatus.ok) { Expected = item.GetType().Name, RowIndex = row });
                run.Cells.Add(new CellResult(ValuesColumn, ResultStatus.ok) { Expected = ScenarioValues.Describe(item), RowIndex = row });
                continue;
            }

            var position = pairs[i];
            if (ordered && position < furthest)
            {
                run.Cells.Add(new CellResult(SetVerificationComparer.OutOfOrderCell, ResultStatus.failed)
                {
                    Note = $"Out of order: {item.GetType().Name} was appended at position {position + 1}, "
                           + $"before one the specification writes ahead of it (position {furthest + 1})",
                    RowIndex = row
                });
            }

            furthest = Math.Max(furthest, position);

            run.Cells.Add(new CellResult(noun, ResultStatus.success)
            {
                Expected = item.GetType().Name,
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
                var happened = ScenarioValues.Describe(actual[position]);
                run.Cells.Add(new CellResult(ValuesColumn, ResultStatus.success, happened)
                {
                    Expected = ScenarioValues.Describe(item),
                    Actual = happened,
                    RowIndex = row
                });
            }
        }

        for (var j = 0; j < actual.Count; j++)
        {
            if (used.Contains(j)) continue;

            run.Cells.Add(new CellResult("extra-row", ResultStatus.invalid)
            {
                Note = $"Unexpected {actual[j].GetType().Name} at position {j + 1}",
                RowIndex = row
            });
            run.Cells.Add(new CellResult(noun, ResultStatus.ok) { Actual = actual[j].GetType().Name, RowIndex = row });
            run.Cells.Add(new CellResult(ValuesColumn, ResultStatus.ok) { Actual = ScenarioValues.Describe(actual[j]), RowIndex = row });
            row++;
        }

        return run;
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

            if (cells.Any(c => c.Name == "missing-row"))
                problems.Add($"MISSING {text(ValuesColumn, true)}");
            else if (cells.Any(c => c.Name == "extra-row"))
                problems.Add($"EXTRA {text(ValuesColumn, false)}");
            else if (cells.FirstOrDefault(c => c.Name == SetVerificationComparer.OutOfOrderCell) is { } order)
                problems.Add($"ORDER {order.Note}");
            else if (cells.FirstOrDefault(c => c.Name == ValuesColumn && c.Status == ResultStatus.failed) is { } fail)
                problems.Add($"FAIL {text(noun, true)}: expected {fail.Expected}, was {fail.Actual}");
        }

        return problems;
    }
}
