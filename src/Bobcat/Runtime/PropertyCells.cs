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

    /// <summary>The comparison itself, with nothing to report it to.</summary>
    public static TableRun Cells(object subject, StepTable expected)
    {
        var run = new TableRun(expected.Headers.ToList());
        var row = expected.AsDictionaries().FirstOrDefault();
        if (row is null) return run;

        var properties = subject.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(ColumnNames.Of, p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var column in expected.Headers)
        {
            if (!row.TryGetValue(column, out var value)) value = "";

            if (!properties.TryGetValue(column, out var property))
            {
                // `invalid`, the same status an unreadable expected cell carries: the document did
                // not disagree, the specification asked about something that does not exist. Naming
                // what IS there is the reader's next move — usually a typo or a renamed property.
                run.Cells.Add(new CellResult(column, ResultStatus.invalid,
                    $"no '{column}' on {subject.GetType().Name} — it has "
                    + string.Join(", ", properties.Keys.OrderBy(k => k)))
                    { RowIndex = 0 });
                continue;
            }

            run.Cells.Add(CellCheck.ForValue(column, property.GetValue(subject), value,
                CheckOptions.Default, 0));
        }

        return run;
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
