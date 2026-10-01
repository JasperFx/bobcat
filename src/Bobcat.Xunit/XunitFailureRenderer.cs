using System.Runtime.CompilerServices;
using Bobcat.Engine;

namespace Bobcat.Xunit;

/// <summary>
/// xUnit's assertion failures, read as the expected/actual pair they already carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what the plug-in point is for.</b> Bobcat core cannot know what
/// <c>Assert.Equal() Failure</c> means or how its message is laid out — it cannot reference xUnit at
/// all, which is the premise of the projected lane. The adapter package for a runner is where that
/// runner's knowledge belongs, and registering it is one line in a module initializer.
/// </para>
/// <para>
/// xUnit's message is:
/// </para>
/// <code>
/// Assert.Equal() Failure: Values differ
/// Expected: 7
/// Actual:   6
/// </code>
/// <para>
/// Parsed, that renders as <c>Assert.Equal(): expected '7', got '6'</c> — the same shape as every
/// other comparison in the report, instead of three lines of prose. Anything it cannot parse falls
/// back to the message verbatim, so it is never worse than before.
/// </para>
/// </remarks>
public sealed class XunitFailureRenderer : ISpecFailureRenderer
{
    [ModuleInitializer]
    internal static void Register() => SpecFailureRenderers.Register(new XunitFailureRenderer());

    public bool Handles(string exceptionTypeName)
        // Every xUnit assertion failure derives from XunitException, and the concrete names are legion
        // (EqualException, TrueException, ContainsException, …). Matching the suffix covers the family,
        // including a consumer's own subclass.
        => exceptionTypeName.EndsWith("Exception", StringComparison.Ordinal)
           && (exceptionTypeName is "XunitException"
               || exceptionTypeName.EndsWith("AssertActualExpectedException", StringComparison.Ordinal)
               || knownAssertions.Contains(exceptionTypeName));

    private static readonly HashSet<string> knownAssertions = new(StringComparer.Ordinal)
    {
        "EqualException", "NotEqualException", "TrueException", "FalseException",
        "NullException", "NotNullException", "SameException", "NotSameException",
        "ContainsException", "DoesNotContainException", "EmptyException", "NotEmptyException",
        "SingleException", "InRangeException", "NotInRangeException", "IsTypeException",
        "IsAssignableFromException", "MatchesException", "StartsWithException", "EndsWithException",
        "FailException", "MultipleException", "DistinctException", "EquivalentException"
    };

    public SpecFailure Render(SpecFailureContext context)
    {
        var cell = parse(context.Message);

        return cell is null
            ? SpecFailure.Assertion(context.Message)
            : SpecFailure.Assertion(context.Message, cell);
    }

    /// <summary><c>Expected: x</c> / <c>Actual: y</c> → one cell, or null.</summary>
    private static CellResult? parse(string message)
    {
        var lines = message.Split('\n').Select(x => x.TrimEnd('\r')).ToList();

        var expected = valueAfter(lines, "Expected:");
        var actual = valueAfter(lines, "Actual:");
        if (expected is null || actual is null) return null;

        // The first line names the assertion — "Assert.Equal() Failure: Values differ" — and its
        // assertion name is the closest thing xUnit gives to a cell name.
        var heading = lines[0].Trim();
        var failureAt = heading.IndexOf(" Failure", StringComparison.Ordinal);
        var name = failureAt > 0 ? heading[..failureAt] : "assertion";

        return new CellResult(name, ResultStatus.failed) { Expected = expected, Actual = actual };
    }

    private static string? valueAfter(List<string> lines, string label)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(label, StringComparison.Ordinal)) continue;

            var value = trimmed[label.Length..].Trim();
            if (value.Length > 0) return value;
        }

        return null;
    }
}
