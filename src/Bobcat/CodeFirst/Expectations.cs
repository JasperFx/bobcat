using System.Collections;
using Bobcat.Engine;
using Bobcat.Engine.Verification;
using Bobcat.Runtime;

namespace Bobcat.CodeFirst;

/// <summary>
/// The fluent tail of a value-observing <c>Then</c>. Attaching an expectation rewrites the step's
/// text ("the balance" → "the balance should be 50") and turns the step into a comparison that
/// records expected and actual side by side in a <see cref="CellResult"/> named <c>result</c> —
/// the same cell a Gherkin <c>[Then]</c> with a return value produces.
/// </summary>
public sealed class ValueExpectation<T>
{
    private const string cellName = "result";

    private readonly string _text;
    private Specification.PendingStep? _pending;
    private Func<T, CellResult>? _check;

    internal ValueExpectation(string text) => _text = text;

    internal void Attach(Specification.PendingStep pending) => _pending = pending;

    internal void Evaluate(T actual, StepResult result)
    {
        if (_check == null)
        {
            // No expectation: the step records what it saw, as an input-style cell.
            result.MarkCells(new CellResult(cellName, ResultStatus.ok) { Actual = RowTable.Format(actual) });
            return;
        }

        var cell = _check(actual);
        result.MarkCells(cell);
        if (cell.Status == ResultStatus.success) result.MarkSuccess();
        else result.MarkFailed();
    }

    /// <summary>Equal by <see cref="EqualityComparer{T}.Default"/>; sequences compare element-wise.</summary>
    public void ShouldBe(T expected)
        => expect($"should be {RowTable.Format(expected)}", actual => cell(areEqual(expected, actual), expected, actual));

    public void ShouldNotBe(T unexpected)
        => expect($"should not be {RowTable.Format(unexpected)}", actual => cell(!areEqual(unexpected, actual),
            $"not {RowTable.Format(unexpected)}", actual));

    public void ShouldBeNull()
        => expect("should be null", actual => cell(actual is null, CellTokens.Null, actual));

    public void ShouldNotBeNull()
        => expect("should not be null", actual => cell(actual is not null, $"not {CellTokens.Null}", actual));

    /// <summary>A predicate with a name for the report: "the total should be positive".</summary>
    public void ShouldSatisfy(Func<T, bool> predicate, string description)
        => expect($"should {description}", actual => cell(predicate(actual), description, actual));

    /// <summary>
    /// Compare against Gherkin cell text through the type-aware <see cref="CellCheck"/> — so
    /// <c>NULL</c>, <c>EMPTY</c>, tolerances and relative times mean what they mean in a
    /// <c>.feature</c> table.
    /// </summary>
    public void ShouldMatch(string expectedText, CheckOptions? options = null)
        => expect($"should be {expectedText}", actual => CellCheck.For(cellName, actual, expectedText, options));

    private void expect(string suffix, Func<T, CellResult> check)
    {
        _check = check;
        if (_pending != null) _pending.Text = $"{_text} {suffix}";
    }

    private static CellResult cell(bool passed, object? expected, T actual)
        => new(cellName, passed ? ResultStatus.success : ResultStatus.failed)
        {
            Expected = expected as string ?? RowTable.Format(expected),
            Actual = RowTable.Format(actual)
        };

    private static bool areEqual(T expected, T actual)
    {
        if (expected is IEnumerable expectedItems && actual is IEnumerable actualItems
            && expected is not string && actual is not string)
        {
            return expectedItems.Cast<object?>().SequenceEqual(actualItems.Cast<object?>());
        }

        return EqualityComparer<T>.Default.Equals(expected, actual);
    }
}

/// <summary>
/// The fluent tail of <c>ThenRows</c>: Storyteller-style set verification from code. Expected rows
/// are any objects whose public properties name the columns — anonymous types read best — and the
/// comparison is the same <see cref="SetVerificationComparer"/> a Gherkin <c>[SetVerification]</c>
/// step uses, so the table renders with the same per-cell colouring, missing and extra rows.
/// </summary>
public sealed class SetExpectation
{
    private readonly string _text;
    private Specification.PendingStep? _pending;
    private string[] _keyColumns = [];
    private IReadOnlyList<Dictionary<string, string>>? _expected;

    internal SetExpectation(string text) => _text = text;

    internal void Attach(Specification.PendingStep pending) => _pending = pending;

    /// <summary>
    /// The columns that identify a row, so a mismatch in another column is reported as a wrong
    /// value on a found row rather than as one missing row plus one extra. Without keys every
    /// column must match for a row to count as found.
    /// </summary>
    public SetExpectation KeyedBy(params string[] columns)
    {
        _keyColumns = columns;
        return this;
    }

    /// <summary>Exactly these rows, in any order: missing and extra rows both fail the step.</summary>
    public void ShouldMatch(params object[] expectedRows)
        => ShouldMatch((IEnumerable<object>)expectedRows);

    /// <inheritdoc cref="ShouldMatch(object[])"/>
    public void ShouldMatch(IEnumerable<object> expectedRows)
    {
        _expected = expectedRows.Select(RowTable.ExpectedRow).ToList();
        if (_pending != null) _pending.Text = $"{_text} should be";
    }

    /// <summary>No rows at all; anything found renders as an extra row.</summary>
    public void ShouldBeEmpty()
    {
        _expected = [];
        if (_pending != null) _pending.Text = $"{_text} should be empty";
    }

    internal void Evaluate(IEnumerable actual, StepResult result)
    {
        if (_expected == null)
        {
            // No expectation: show what was there.
            RowTable.Describe(actual.Cast<object?>()).ApplyTo(result);
            return;
        }

        if (_expected.Count == 0)
        {
            var rows = actual.Cast<object?>().ToList();
            if (rows.Count == 0)
            {
                result.MarkSuccess();
                return;
            }

            // SetVerificationComparer has no columns to report extras against when nothing was
            // expected, so describe the intruders ourselves and fail.
            var table = RowTable.Describe(rows);
            result.IsSetVerification = true;
            result.SetVerificationColumns = table.Columns;
            result.MarkCells(rows.Select((_, index) => new CellResult("extra-row", ResultStatus.invalid,
                "Extra row: " + string.Join(", ", table.Columns.Select(column =>
                    $"{column}={table.Cells.First(c => c.RowIndex == index && c.Name == column).Actual}")))
                { RowIndex = index }).ToArray());
            result.MarkFailed();
            return;
        }

        SetVerificationComparer.Compare(actual, _expected, _keyColumns, result);
    }
}
