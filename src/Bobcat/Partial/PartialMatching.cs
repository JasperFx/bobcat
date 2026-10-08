using Bobcat.Engine;
using Bobcat.Engine.Verification;
using Bobcat.Runtime;

namespace Bobcat;

/// <summary>
/// Comparing an actual object with an expectation (bobcat#418), whichever form it takes:
/// <list type="bullet">
/// <item>a partial object (<c>Specify&lt;T&gt;()</c> or a table row) — <b>only</b> the members it names
/// are judged, and only those are shown;</item>
/// <item>an <see cref="IExpectedValue"/> — every member except the ignored ones;</item>
/// <item>a plain object — every member, structurally.</item>
/// </list>
/// </summary>
public static class PartialMatching
{
    /// <summary>The members where <paramref name="actual"/> disagrees with <paramref name="expected"/>; empty when they agree.</summary>
    public static IReadOnlyList<ValueDifference> Differences(object actual, object expected) => expected switch
    {
        IPartialObject partial => partialDifferences(actual, partial),
        IExpectedValue value => ObjectComparison.Differences(actual, value.Value, value.IgnoredPaths),
        _ => ObjectComparison.Differences(actual, expected)
    };

    /// <summary>Whether <paramref name="actual"/> is the expected type and agrees on what <paramref name="expected"/> judges.</summary>
    public static bool Matches(object actual, object expected)
        => actual.GetType() == ExpectedType(expected) && Differences(actual, expected).Count == 0;

    /// <summary>The type an expectation is for.</summary>
    public static Type ExpectedType(object expected) => expected switch
    {
        IPartialObject partial => partial.Type,
        IExpectedValue value => value.Value.GetType(),
        Type type => type,
        _ => expected.GetType()
    };

    /// <summary>
    /// The expectation's values on one line: a partial object's specified members only
    /// (<c>TrackingNumber: 1Z999</c>), otherwise every property.
    /// </summary>
    public static string DescribeExpected(object expected) => expected switch
    {
        IPartialObject partial => PartialObjects.DescribeValues(partial),
        IExpectedValue value => ScenarioValues.DescribeProperties(value.Value),
        Type => "",
        _ => ScenarioValues.DescribeProperties(expected)
    };

    /// <summary>
    /// What <paramref name="actual"/> holds, in the terms of <paramref name="expected"/>: for a partial
    /// object only the members it specifies, so the unspecified rest never appears in a result.
    /// </summary>
    public static string DescribeActual(object actual, object expected)
    {
        if (expected is not IPartialObject partial) return ScenarioValues.DescribeProperties(actual);

        return string.Join(", ", partial.Values.Select(v =>
        {
            var resolved = PropertyCells.Resolve(actual, v.Path);
            var shown = resolved.Kind == PropertyCells.PathResultKind.Found
                ? ScenarioValues.Format(ObjectComparison.Unwrap(resolved.Value))
                : "?";
            return $"{v.Path}: {shown}";
        }));
    }

    private static IReadOnlyList<ValueDifference> partialDifferences(object actual, IPartialObject partial)
    {
        var differences = new List<ValueDifference>();
        foreach (var specified in partial.Values)
        {
            var resolved = PropertyCells.Resolve(actual, specified.Path);
            if (resolved.Kind == PropertyCells.PathResultKind.NoSuchProperty)
                throw new SpecCriticalException(
                    $"{partial.Type.Name} has no member '{specified.Path}' to compare: {resolved.Message}. "
                    + "Check the spelling, or the member may have been renamed since this spec was written.");

            // A null partway along the path is the actual object disagreeing, not the spec being wrong
            var actualValue = resolved.Kind == PropertyCells.PathResultKind.Found
                ? ObjectComparison.Unwrap(resolved.Value)
                : null;

            if (!agrees(actualValue, specified))
                differences.Add(new ValueDifference(specified.Path,
                    specified.IsText ? ((string?)specified.Value)?.Trim() : specified.Value, actualValue));
        }

        return differences;
    }

    private static bool agrees(object? actual, SpecifiedValue specified)
    {
        if (specified.IsText)
        {
            // The cell rules every table uses: NULL, EMPTY, relative times, numeric text
            var cell = CellCheck.ForValue(specified.Path, actual, (string?)specified.Value ?? "", CheckOptions.Default);
            return cell.Status == ResultStatus.success;
        }

        var expected = ObjectComparison.Unwrap(specified.Value);
        if (expected is string text && actual is not null and not string)
        {
            // A string for a member that is not one reads as a cell, as it does when building
            return CellCheck.ForValue(specified.Path, actual, text, CheckOptions.Default).Status == ResultStatus.success;
        }

        return ObjectComparison.Compare(actual, expected).All(x => x.Matched);
    }

}
