using Bobcat.Engine;

namespace Bobcat.Runtime;

/// <summary>
/// How far apart two values may be and still agree (bobcat#456) — an absolute amount for a number, a
/// window for a date or a time.
/// </summary>
/// <remarks>
/// The note it renders — <c>±0.01</c> — is the one a table cell already shows for
/// <see cref="Engine.Verification.CheckOptions.Tolerance"/>, so a typed comparison and a cell
/// comparison say the same thing about the same slack.
/// </remarks>
public readonly record struct Tolerance(double? Amount, TimeSpan? Window)
{
    /// <summary>Within <paramref name="amount"/>, either way.</summary>
    public static Tolerance Of(double amount) => new(Math.Abs(amount), null);

    /// <summary>Within <paramref name="window"/>, either way.</summary>
    public static Tolerance Of(TimeSpan window) => new(null, window.Duration());

    /// <summary>How the slack reads in a cell: <c>±0.01</c>, <c>±00:00:02</c>.</summary>
    public string Note => Window is { } window ? $"±{window}" : $"±{Amount}";

    /// <summary>
    /// Whether <paramref name="actual"/> is within this of <paramref name="expected"/>.
    /// </summary>
    /// <exception cref="SpecCriticalException">
    /// The member is of no shape this tolerance can measure — a window over a string, an amount over a
    /// date. A specification asking for slack on a value that has none is a mistake in the
    /// specification, not a disagreement, so it says so rather than quietly comparing exactly.
    /// </exception>
    public bool Allows(object? actual, object? expected, string path)
    {
        if (actual is null || expected is null) return actual is null && expected is null;

        if (Window is { } window)
        {
            if (ticksOf(actual) is { } actualTicks && ticksOf(expected) is { } expectedTicks)
                return Math.Abs(actualTicks - expectedTicks) <= window.Ticks;

            throw new SpecCriticalException(
                $"'{path}' is expected within {window}, but it is a {actual.GetType().Name} rather than a date or "
                + "a time. Use Approximately(...) for a number.");
        }

        if (numberOf(actual) is { } actualValue && numberOf(expected) is { } expectedValue)
            return Math.Abs(actualValue - expectedValue) <= Amount!.Value;

        throw new SpecCriticalException(
            $"'{path}' is expected within {Amount}, but it is a {actual.GetType().Name} rather than a number. "
            + "Use Within(...) for a date or a time.");
    }

    private static long? ticksOf(object value) => value switch
    {
        DateTime at => at.Ticks,
        // Compared in UTC, so two offsets naming the same instant are the same instant
        DateTimeOffset at => at.UtcTicks,
        DateOnly on => on.ToDateTime(TimeOnly.MinValue).Ticks,
        TimeOnly at => at.Ticks,
        TimeSpan span => span.Ticks,
        _ => null
    };

    private static double? numberOf(object value) => value switch
    {
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToDouble(value),
        _ => null
    };
}

/// <summary>
/// What a comparison may not judge strictly: members it shows without judging, members with slack, and
/// collection members whose order is part of the claim. Built by <see cref="ExpectedValue{T}" />
/// (bobcat#456); <see cref="None" /> is the ordinary strict comparison.
/// </summary>
/// <remarks>
/// <b>Paths are from the object being compared</b>, which is why a rule does not reach inside a
/// collection's elements: an element has no path a specification writes, since indexes appear in what a
/// comparison reports and never in what a specification says. An element that needs its own slack is
/// its own expectation — <c>Expect.Exactly(Expect.Value(line).Approximately(x =&gt; x.Price, 0.01))</c>.
/// </remarks>
public sealed record ComparisonRules
{
    /// <summary>Judge everything, strictly.</summary>
    public static readonly ComparisonRules None = new();

    /// <summary>Paths shown but not compared. A path also covers everything beneath it.</summary>
    public IReadOnlyCollection<string> Ignored { get; init; } = Array.Empty<string>();

    /// <summary>Per path, how far apart the two values may be.</summary>
    public IReadOnlyDictionary<string, Tolerance> Tolerances { get; init; } =
        new Dictionary<string, Tolerance>(StringComparer.Ordinal);

    /// <summary>
    /// Collection members whose order is part of the claim. A collection is matched in any order
    /// otherwise, which is what a bare one means.
    /// </summary>
    public IReadOnlyCollection<string> Ordered { get; init; } = Array.Empty<string>();

    /// <summary>Nothing to apply, so a comparison need not look anything up.</summary>
    public bool IsNone => Ignored.Count == 0 && Tolerances.Count == 0 && Ordered.Count == 0;

    /// <summary>Whether <paramref name="path"/> is shown but not judged.</summary>
    public bool Ignores(string path) => Ignored.Count > 0 && Ignored.Any(x => covers(x, path));

    /// <summary>The slack at <paramref name="path"/>, if any.</summary>
    public Tolerance? ToleranceFor(string path)
        => Tolerances.Count > 0 && Tolerances.TryGetValue(path, out var tolerance) ? tolerance : null;

    /// <summary>Whether the collection at <paramref name="path"/> must be in the order written.</summary>
    public bool IsOrdered(string path)
        => Ordered.Count > 0 && Ordered.Contains(path, StringComparer.Ordinal);

    /// <summary>Only the ignored paths, as the comparison used to take them.</summary>
    public static ComparisonRules Ignoring(IReadOnlyCollection<string>? ignored)
        => ignored is null || ignored.Count == 0 ? None : new ComparisonRules { Ignored = ignored };

    private static bool covers(string ignored, string path)
        => path == ignored || path.StartsWith(ignored + ".", StringComparison.Ordinal) ||
           path.StartsWith(ignored + "[", StringComparison.Ordinal);
}
