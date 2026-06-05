namespace Bobcat.Engine.Verification;

/// <summary>
/// The structured result of a typed value comparison: an outcome plus the
/// formatted expected/actual strings and an optional note. This is what an
/// <see cref="IValueChecker{T}"/> returns; <see cref="CellCheck"/> turns it into a
/// <see cref="CellResult"/>.
/// </summary>
public sealed class CheckResult
{
    public CheckResult(CheckOutcome outcome, string expected, string actual, string? note = null)
    {
        Outcome = outcome;
        Expected = expected;
        Actual = actual;
        Note = note;
    }

    public CheckOutcome Outcome { get; }

    /// <summary>Formatted expected value.</summary>
    public string Expected { get; }

    /// <summary>Formatted actual value.</summary>
    public string Actual { get; }

    /// <summary>Optional note — tolerance applied, parse failure reason, etc.</summary>
    public string? Note { get; }

    public static CheckResult Match(string expected, string actual, string? note = null)
        => new(CheckOutcome.Match, expected, actual, note);

    public static CheckResult Mismatch(string expected, string actual, string? note = null)
        => new(CheckOutcome.Mismatch, expected, actual, note);

    public static CheckResult Invalid(string expected, string actual, string? note = null)
        => new(CheckOutcome.Invalid, expected, actual, note);
}
