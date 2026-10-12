namespace Bobcat.Engine.Verification;

/// <summary>
/// Per-comparison options, passed to <c>SpecAssert.Check</c> (or <see cref="CellCheck"/>) and carried
/// into an <see cref="IValueChecker{T}"/>.
/// </summary>
public sealed class CheckOptions
{
    /// <summary>Shared default — case-sensitive, trimming, no tolerance.</summary>
    public static readonly CheckOptions Default = new();

    /// <summary>
    /// Absolute tolerance for numeric comparisons — <c>SpecAssert.Check(..., tolerance:)</c> is the
    /// shorthand. Null means exact comparison.
    /// </summary>
    public double? Tolerance { get; init; }

    /// <summary>
    /// Whether string comparison (and enum name matching) is case sensitive.
    /// Defaults to true.
    /// </summary>
    public bool CaseSensitive { get; init; } = true;

    /// <summary>
    /// Whether to trim leading/trailing whitespace from the expected text before
    /// comparing. String checkers honor an explicit double-quote wrapper to opt out
    /// per-value. Defaults to true.
    /// </summary>
    public bool Trim { get; init; } = true;

    /// <summary>
    /// An explicit checker type. When set this
    /// takes precedence over every other entry in the resolution chain. The type must
    /// implement <see cref="IValueChecker{T}"/> for the value being checked and have a
    /// parameterless constructor.
    /// </summary>
    public Type? ComparisonType { get; init; }
}
