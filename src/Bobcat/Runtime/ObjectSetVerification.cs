using Bobcat.Engine;

namespace Bobcat.Runtime;

/// <summary>One leaf of an expected object that the actual one disagreed on.</summary>
/// <param name="Note">
/// Why, where the two values alone do not say it (bobcat#456) — an item of a collection that was
/// MISSING, EXTRA, out of order or present when forbidden, or a member of the wrong type. Trailing and
/// optional, so a caller that ignores it is exactly as correct as it was.
/// </param>
public sealed record ValueDifference(string Path, object? Expected, object? Actual, string? Note = null);

/// <summary>
/// How a list of disagreements reads in one cell — <b>in one place</b>, so the grid, the sentence a
/// test throws and the report cannot word the same failure differently.
/// </summary>
public static class ValueDifferences
{
    /// <summary>
    /// How many disagreements are shown before "…and N more". A reordered fifty-element collection is a
    /// hundred disagreements, and a cell holding all of them is unreadable; the cap is here, where they
    /// are rendered, so a caller reading them programmatically still gets every one.
    /// </summary>
    public const int Shown = 10;

    /// <summary>
    /// The <paramref name="expected"/> or actual side of each disagreement, as <c>path: value</c> pairs.
    /// A note — MISSING, EXTRA, out of order — is shown on both sides, because it is the reason the two
    /// values do not speak for themselves.
    /// </summary>
    public static string Describe(IReadOnlyList<ValueDifference> differences, bool expected, string? member = null)
    {
        var text = string.Join(", ", differences.Take(Shown).Select(difference =>
        {
            var path = trim(difference.Path, member);
            var value = ObjectComparison.Format(expected ? difference.Expected : difference.Actual);
            var named = path.Length == 0 ? value : $"{path}: {value}";

            return difference.Note is null ? named : $"{named} ({difference.Note})";
        }));

        return differences.Count > Shown ? $"{text}, …and {differences.Count - Shown} more" : text;
    }

    /// <summary>One sentence per disagreement, for the message a test throws when nothing shows a grid.</summary>
    public static IReadOnlyList<string> Sentences(IReadOnlyList<ValueDifference> differences)
        => differences.Take(Shown)
            .Select(difference =>
            {
                var sentence = $"{difference.Path}: expected {ObjectComparison.Format(difference.Expected)}, "
                               + $"was {ObjectComparison.Format(difference.Actual)}";
                return difference.Note is null ? sentence : $"{difference.Path}: {difference.Note}";
            })
            .Concat(differences.Count > Shown ? [$"…and {differences.Count - Shown} more"] : Array.Empty<string>())
            .ToList();

    // Inside a cell that is already a member's, the member's own name adds nothing to every path
    private static string trim(string path, string? member)
    {
        if (member is null || !path.StartsWith(member, StringComparison.OrdinalIgnoreCase)) return path;

        var rest = path.Substring(member.Length);
        return rest.StartsWith('.') ? rest.Substring(1) : rest;
    }
}

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

    /// <summary>
    /// How a misplaced item reads, in one place so a top-level set and a collection member inside an
    /// object say it the same way. Positions are 0-based and read out 1-based.
    /// </summary>
    public static string OutOfOrderNote(string typeName, int position, int before)
        => $"Out of order: {typeName} was appended at position {position + 1}, "
           + $"before one the specification writes ahead of it (position {before + 1})";

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
    /// <see cref="Verify(IReadOnlyList{object}, IReadOnlyList{object}, string, SetMode)" /> for items that carry
    /// tags (JasperFx/wolverine#4865) — the events a command appended, with the tags they were appended with.
    /// An expectation that names tags (<see cref="ITaggedExpectation" />) matches only an item whose tags,
    /// from <paramref name="tagsOf"/>, are exactly those, in any order; its row shows both sides' tags.
    /// </summary>
    public static TableRun Verify(IReadOnlyList<object> actual, IReadOnlyList<object> expected, string noun,
        SetMode mode, Func<object, IReadOnlyList<object>?> tagsOf)
        => cells(actual, expected, (item, i) => PartialMatching.Differences(item, expected[i], tagsOf(item)), noun, mode, tagsOf);

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
    /// An ordered comparison seeds that matching with the longest pairing that keeps the specification's
    /// order, so a row is only ORDER when no pairing avoids it (bobcat#476).
    /// </remarks>
    public static TableRun Cells(
        IReadOnlyList<object> actual,
        IReadOnlyList<object> expected,
        Func<object, int, IReadOnlyList<ValueDifference>> compare,
        string noun,
        SetMode mode)
        => cells(actual, expected, compare, noun, mode, null);

    /// <summary>
    /// How a set of expectations paired with the items that actually happened — the matching on its
    /// own, with nothing rendered. Extracted (bobcat#456) so that a collection <em>inside</em> an
    /// object is matched by the same rules as a top-level set rather than position by position:
    /// one matching, two renderings.
    /// </summary>
    /// <param name="Pairs">Expectation index to the actual index it took, or -1 for one that found nothing.</param>
    /// <param name="Owner">Actual index to the expectation that took it, or -1 for an item nobody expected.</param>
    /// <param name="Differences">
    /// Per expectation: null when it matched exactly or found nothing, otherwise what it disagreed with
    /// its paired item about — the near match that is a FAIL rather than a MISSING beside an EXTRA.
    /// </param>
    /// <param name="OutOfOrder">
    /// Per expectation: -1 when in order, otherwise the actual position of the furthest expectation
    /// written ahead of it, which it came before. Always -1 unless the mode is <see cref="SetMode.Ordered"/>.
    /// </param>
    public sealed record SetPairing(
        int[] Pairs,
        int[] Owner,
        IReadOnlyList<ValueDifference>?[] Differences,
        int[] OutOfOrder);

    /// <summary>
    /// Pair <paramref name="expected"/> with <paramref name="actual"/>. See <see cref="Cells(IReadOnlyList{object},
    /// IReadOnlyList{object}, Func{object, int, IReadOnlyList{ValueDifference}}, string, SetMode)"/>'s remarks for
    /// why the pairing is a maximum matching rather than first come first served.
    /// </summary>
    public static SetPairing Pair(
        IReadOnlyList<object> actual,
        IReadOnlyList<object> expected,
        Func<object, int, IReadOnlyList<ValueDifference>> compare,
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

        var ordered = mode == SetMode.Ordered;

        // Pass 1: exact matches, as many as any pairing can make. An ordered comparison starts from
        // the longest pairing that keeps the specification's order (bobcat#476), so ORDER is only
        // reported when no pairing of that size avoids it: two identical expectations can each match
        // the other's actual, and Kuhn's algorithm alone happily pairs them crosswise.
        if (ordered) seedInOrder(exact, pairs, owner);

        for (var i = 0; i < expected.Count; i++)
        {
            if (pairs[i] >= 0) continue;
            augment(i, exact, pairs, owner, new bool[actual.Count], ordered);
        }

        var differences = new IReadOnlyList<ValueDifference>?[expected.Count];

        // Pass 2: the same type with different values — a FAIL row, not MISSING beside EXTRA.
        // Only for an item that HAS members to disagree about: pairing two scalars says "expected
        // rush, was vip" about two items that have nothing to do with each other, where MISSING
        // beside EXTRA is the truth.
        for (var i = 0; i < expected.Count; i++)
        {
            if (pairs[i] >= 0) continue;
            var type = PartialMatching.ExpectedType(expected[i]);
            if (ObjectComparison.IsLeaf(type)) continue;
            var candidate = -1;
            var (after, before) = ordered ? gap(pairs, i, actual.Count) : (-1, actual.Count);
            for (var j = 0; j < actual.Count; j++)
            {
                if (owner[j] >= 0 || actual[j].GetType() != type) continue;

                // In an ordered comparison, an item that sits where this row belongs beats the first
                // one found, which could be behind a row written earlier: a spurious ORDER beside the FAIL
                if (j > after && j < before)
                {
                    candidate = j;
                    break;
                }

                if (candidate < 0) candidate = j;
                if (!ordered) break;
            }

            if (candidate >= 0)
            {
                pairs[i] = candidate;
                owner[candidate] = i;
                differences[i] = compare(actual[candidate], i);
            }
        }

        // Which pairings came before one the specification writes ahead of them. An expectation that
        // found nothing does not move the mark: a missing item must not read as reordering the rest.
        var outOfOrder = new int[expected.Count];
        Array.Fill(outOfOrder, -1);
        var mark = -1;
        for (var i = 0; i < expected.Count; i++)
        {
            if (pairs[i] < 0) continue;
            if (ordered && pairs[i] < mark) outOfOrder[i] = mark;
            mark = Math.Max(mark, pairs[i]);
        }

        return new SetPairing(pairs, owner, differences, outOfOrder);
    }

    private static TableRun cells(
        IReadOnlyList<object> actual,
        IReadOnlyList<object> expected,
        Func<object, int, IReadOnlyList<ValueDifference>> compare,
        string noun,
        SetMode mode,
        Func<object, IReadOnlyList<object>?>? tagsOf)
    {
        var (pairs, owner, differences, outOfOrder) = Pair(actual, expected, compare, mode);

        var columns = new List<string> { noun, ValuesColumn };
        var run = new TableRun(columns);
        var row = 0;

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
            if (outOfOrder[i] >= 0)
            {
                run.Cells.Add(new CellResult(SetVerificationComparer.OutOfOrderCell, ResultStatus.failed)
                {
                    Note = OutOfOrderNote(typeName, position, outOfOrder[i]),
                    RowIndex = row
                });
            }

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
                    Expected = ValueDifferences.Describe(diffs, expected: true),
                    Actual = ValueDifferences.Describe(diffs, expected: false),
                    RowIndex = row
                });
            }
            else
            {
                // Shows what HAPPENED. The two agree on everything the comparison judged, but a member
                // the caller chose to ignore — a minted timestamp — only has a real value on this side.
                // For a partial expectation it is only the members the expectation names.
                var happened = tagsOf is null
                    ? PartialMatching.DescribeActual(actual[position], item)
                    : PartialMatching.DescribeActual(actual[position], item, tagsOf(actual[position]));
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
        => absent(actual, forbidden, noun, null);

    /// <summary>
    /// <see cref="Absent(IReadOnlyList{object}, IReadOnlyList{object}, string)" /> for items that carry tags
    /// (JasperFx/wolverine#4865): a forbidden item that names tags forbids only an item carrying exactly those.
    /// </summary>
    public static TableRun Absent(IReadOnlyList<object> actual, IReadOnlyList<object> forbidden, string noun,
        Func<object, IReadOnlyList<object>?> tagsOf)
        => absent(actual, forbidden, noun, tagsOf);

    private static TableRun absent(IReadOnlyList<object> actual, IReadOnlyList<object> forbidden, string noun,
        Func<object, IReadOnlyList<object>?>? tagsOf)
    {
        var run = new TableRun([noun, ValuesColumn]);
        for (var i = 0; i < forbidden.Count; i++)
        {
            var item = forbidden[i];
            var type = PartialMatching.ExpectedType(item);
            var found = actual.FirstOrDefault(a => item is Type
                ? a.GetType() == type
                : tagsOf is null ? PartialMatching.Matches(a, item) : PartialMatching.Matches(a, item, tagsOf(a)));
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
                Actual = item is Type
                    ? ScenarioValues.DescribeProperties(found)
                    : tagsOf is null ? PartialMatching.DescribeActual(found, item) : PartialMatching.DescribeActual(found, item, tagsOf(found)),
                RowIndex = i
            });
        }

        return run;
    }

    // Kuhn's augmenting path: give expectation i an actual item, moving an earlier pairing to another
    // item it also matches when that frees one up. With preferFree, an unpaired item it matches is
    // taken before any pairing is disturbed, so an ordered seed is only rearranged when that grows it.
    private static bool augment(int i, bool[,] exact, int[] pairs, int[] owner, bool[] visited, bool preferFree)
    {
        if (preferFree)
        {
            for (var j = 0; j < owner.Length; j++)
            {
                if (!exact[i, j] || visited[j] || owner[j] >= 0) continue;

                pairs[i] = j;
                owner[j] = i;
                return true;
            }
        }

        for (var j = 0; j < owner.Length; j++)
        {
            if (!exact[i, j] || visited[j]) continue;
            visited[j] = true;

            if (owner[j] < 0 || augment(owner[j], exact, pairs, owner, visited, preferFree))
            {
                pairs[i] = j;
                owner[j] = i;
                return true;
            }
        }

        return false;
    }

    // The longest pairing that keeps both sides in order — a longest common subsequence over the exact
    // matches. The walk back takes a diagonal whenever it is as long as skipping, so expected[i] keeps
    // actual[i] whenever the two sequences agree position by position.
    private static void seedInOrder(bool[,] exact, int[] pairs, int[] owner)
    {
        var expectedCount = pairs.Length;
        var actualCount = owner.Length;
        var longest = new int[expectedCount + 1, actualCount + 1];

        for (var i = expectedCount - 1; i >= 0; i--)
        {
            for (var j = actualCount - 1; j >= 0; j--)
            {
                longest[i, j] = exact[i, j]
                    ? Math.Max(1 + longest[i + 1, j + 1], Math.Max(longest[i + 1, j], longest[i, j + 1]))
                    : Math.Max(longest[i + 1, j], longest[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < expectedCount && y < actualCount)
        {
            if (exact[x, y] && longest[x, y] == 1 + longest[x + 1, y + 1])
            {
                pairs[x] = y;
                owner[y] = x;
                x++;
                y++;
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
    }

    // The positions an item for expectation i must fall strictly between to stay in order with the
    // nearest paired expectations either side of it.
    private static (int after, int before) gap(int[] pairs, int i, int actualCount)
    {
        var after = -1;
        for (var k = i - 1; k >= 0; k--)
        {
            if (pairs[k] < 0) continue;
            after = pairs[k];
            break;
        }

        var before = actualCount;
        for (var k = i + 1; k < pairs.Length; k++)
        {
            if (pairs[k] < 0) continue;
            before = pairs[k];
            break;
        }

        return (after, before);
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
