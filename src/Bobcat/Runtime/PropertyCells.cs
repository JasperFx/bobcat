using System.Reflection;
using Bobcat.Engine;
using Bobcat.Engine.Verification;

namespace Bobcat.Runtime;

/// <summary>
/// Compares one object against one table row — every column the row names against the property of
/// that name — and reports the comparison as a one-row grid.
/// </summary>
/// <remarks>
/// <para>
/// <b>Storyteller's <c>VerifyObject</c> / <c>CheckPropertyGrammar</c>, and general-purpose</b>: any
/// step with an object and a row can call it, and <c>Fixture.VerifyObject</c> is the wrapper
/// (issue #395). It arrived as the engine behind two event-store grammars, which is where it came
/// from and not what it is.
/// </para>
/// <para>
/// Those two are <c>Then the {readmodel} read model contains</c> and
/// <c>Then the {document} with id {string} has</c>. Both used to flatten the whole
/// comparison into one exception message —
/// <c>"AppointmentsQueue read model did not match: AwaitingConfirmation: expected 0, was 1;
/// Confirmed: expected 0, was -1"</c> — which is a sentence a reader has to parse to find the one
/// column that disagreed, and three green columns nobody ever saw. As cells it is a grid with a
/// verdict per column (issue #384).
/// </para>
/// <para>
/// <b>Not a set verification of one row.</b> A set matches rows by key columns, so a single wrong
/// value there is a missing row beside an extra one; here the subject is known and the columns are
/// the claim, so a wrong value is one failed cell. Different question, different comparison — but the
/// same <see cref="CellCheck"/> underneath, so a document column and a set column disagree in the
/// same words, and the same <see cref="ColumnNames"/>, so a property titled by
/// <see cref="HeaderAttribute"/> is titled here too.
/// </para>
/// </remarks>
public static class PropertyCells
{
    /// <summary>
    /// Compare <paramref name="subject"/> against the first row of <paramref name="expected"/> and
    /// report the grid to whichever lane is listening.
    /// </summary>
    /// <returns>The grid, whose <c>Succeeded</c> is the comparison's verdict.</returns>
    public static TableRun Verify(object subject, StepTable expected, IStepContext? context = null)
    {
        var run = Cells(subject, expected);
        run.Report(context);
        return run;
    }

    /// <summary>
    /// Compare <paramref name="subject"/> against a partial object — <c>Specify&lt;T&gt;()</c> or a table
    /// row — judging and showing only the members it names (bobcat#418), and report the grid.
    /// </summary>
    public static TableRun Verify(object subject, IPartialObject expected, IStepContext? context = null)
    {
        var run = new TableRun(expected.Values.Select(v => v.Path).ToList());
        var differences = PartialMatching.Differences(subject, expected)
            .ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var value in expected.Values)
        {
            var shown = Resolve(subject, value.Path) is { Kind: PathResultKind.Found } found
                ? ScenarioValues.Format(ObjectComparison.Unwrap(found.Value))
                : CellTokens.Null;

            // A check (bobcat#450) is shown as its assertion reads, and a failed one says why
            var check = value.Value as MemberCheck;
            run.Cells.Add(differences.TryGetValue(value.Path, out var difference)
                ? new CellResult(value.Path, ResultStatus.failed)
                {
                    Expected = check?.Description ?? ScenarioValues.Format(difference.Expected), Actual = shown, RowIndex = 0,
                    Note = check?.Run(difference.Actual)
                }
                : new CellResult(value.Path, ResultStatus.success, shown)
                {
                    Expected = check?.Description ?? shown, Actual = shown, RowIndex = 0
                });
        }

        run.Report(context);
        return run;
    }

    /// <summary>The comparison itself, with nothing to report it to.</summary>
    public static TableRun Cells(object subject, StepTable expected)
    {
        var run = new TableRun(expected.Headers.ToList());
        var row = expected.AsDictionaries().FirstOrDefault();
        if (row is null) return run;

        foreach (var column in expected.Headers)
        {
            if (!row.TryGetValue(column, out var value)) value = "";

            run.Cells.Add(Resolve(subject, column) switch
            {
                { Kind: PathResultKind.Found } found =>
                    CellCheck.ForValue(column, found.Value, value, CheckOptions.Default, 0),

                // `invalid`, the same status an unreadable expected cell carries: the subject did
                // not disagree, the specification asked about something that does not exist. Naming
                // what IS there is the reader's next move — usually a typo or a renamed property.
                { Kind: PathResultKind.NoSuchProperty } missing =>
                    new CellResult(column, ResultStatus.invalid, missing.Message!) { RowIndex = 0 },

                // A null partway along the path is the SUBJECT disagreeing, not the specification
                // being wrong — so it is a failed comparison naming the segment that was null,
                // which is decidable only at run time and is a different claim from `invalid`.
                var stopped =>
                    new CellResult(column, ResultStatus.failed)
                    {
                        Expected = value, Actual = CellTokens.Null,
                        Note = stopped.Message, RowIndex = 0
                    }
            });
        }

        return run;
    }

    /// <summary>How deep a dotted column may reach, so a cyclic graph cannot hang a comparison.</summary>
    public const int MaxDepth = 8;

    /// <summary>What resolving a column against a subject produced.</summary>
    public enum PathResultKind
    {
        /// <summary>The path resolved; <see cref="PathResult.Value"/> is the value, possibly null.</summary>
        Found,

        /// <summary>A segment names no property on the type it was resolved against.</summary>
        NoSuchProperty,

        /// <summary>A segment resolved to null, so the rest of the path has nothing to read.</summary>
        StoppedAtNull
    }

    /// <param name="Value">The resolved value, for <see cref="PathResultKind.Found"/> only.</param>
    /// <param name="Message">The reader's next move, for everything else.</param>
    public record PathResult(PathResultKind Kind, object? Value, string? Message);

    /// <summary>
    /// Resolve <paramref name="column"/> against <paramref name="subject"/>, following <c>.</c> into
    /// nested properties — <c>Address.City</c> (issue #411).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Public, and here rather than in any one grammar</b>, because four callers share this
    /// lookup: the two shipped event-store grammars, <c>Fixture.VerifyObject</c>, and the Wolverine
    /// side's event and HTTP-response assertions. Solving it in one of them would leave the others
    /// with a different rule for what a column name means — the drift <see cref="ColumnNames"/> and
    /// <see cref="CellCheck"/> exist to prevent.
    /// </para>
    /// <para>
    /// <b>Titling applies per segment</b>, through <see cref="ColumnNames.Of(PropertyInfo)"/>, so a
    /// property renamed by <c>[Header]</c> stays addressable by its header at every depth rather
    /// than only at the top.
    /// </para>
    /// <para>
    /// <b>Collection indexers are deliberately unsupported.</b> <c>Items[0].Sku</c> is a question a
    /// set verification answers properly: it matches rows by key columns, where a path into a
    /// collection would make one wrong value read as a missing row beside an extra one — the exact
    /// confusion this type's own notes warn about. The message says so rather than failing silently.
    /// </para>
    /// </remarks>
    public static PathResult Resolve(object subject, string column)
    {
        if (column.Contains('[') || column.Contains(']'))
        {
            return new PathResult(PathResultKind.NoSuchProperty, null,
                $"'{column}' indexes a collection, which a property check cannot follow — "
                + "assert a collection with a set verification instead");
        }

        var segments = column.Split('.', StringSplitOptions.TrimEntries);

        if (segments.Length > MaxDepth)
        {
            return new PathResult(PathResultKind.NoSuchProperty, null,
                $"'{column}' is {segments.Length} levels deep and the limit is {MaxDepth}");
        }

        object? current = subject;

        for (var i = 0; i < segments.Length; i++)
        {
            var owner = current!.GetType();
            var properties = owner
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToDictionary(ColumnNames.Of, p => p, StringComparer.OrdinalIgnoreCase);

            if (!properties.TryGetValue(segments[i], out var property))
            {
                // Named at the depth that failed, not at the top: "no 'City' on Address" is the
                // reader's next move, where the subject's own property list would be the least
                // useful half of the sentence.
                return new PathResult(PathResultKind.NoSuchProperty, null,
                    $"no '{segments[i]}' on {owner.Name} — it has "
                    + string.Join(", ", properties.Keys.OrderBy(k => k)));
            }

            current = property.GetValue(current);

            var isLast = i == segments.Length - 1;
            if (current is null && !isLast)
            {
                return new PathResult(PathResultKind.StoppedAtNull, null,
                    $"{string.Join('.', segments.Take(i + 1))} was null");
            }
        }

        return new PathResult(PathResultKind.Found, current, null);
    }

    /// <summary>
    /// The columns that disagreed, for a message that has to be short because the grid already said
    /// it at length — a CI log tailing one line still needs to know which column to look at.
    /// </summary>
    public static IReadOnlyList<string> Disagreeing(TableRun run)
        => run.Cells
            .Where(c => c.Status is not (ResultStatus.ok or ResultStatus.success))
            .Select(c => c.Name)
            .ToList();
}
