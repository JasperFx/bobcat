using System.Collections;
using System.Globalization;
using System.Reflection;
using Bobcat.Partial;

namespace Bobcat.Runtime;

/// <summary>
/// Structural comparison of two object graphs, property by property and never through
/// <c>Equals</c>: record equality is silently wrong for a collection, which compares by reference.
/// Moved here from WolverineFx.Bobcat (wolverine#4835) so there is one comparison (bobcat#418).
/// </summary>
/// <remarks>
/// <para>
/// A graph is flattened to its <b>leaves</b> — <c>Address.City</c>, <c>Lines[0].Sku</c>,
/// <c>Lines.Count</c> — and each leaf is compared and rendered as its own cell, so a deep graph is
/// still a cell table rather than one cell holding a document diff.
/// </para>
/// <para>
/// <b>Indexes appear in what this reports, never in what a spec writes.</b> A leaf path such as
/// <c>Lines[0].Sku</c> names where two whole graphs differ; a specification naming a member
/// (<c>PropertyCells</c>, a partial object) may not index into a collection, because that question
/// belongs to a set verification. The two rules answer different questions and do not conflict.
/// </para>
/// <para>
/// F# options and single-case unions compare by what they hold, so <c>Some "UPS"</c> agrees with
/// <c>"UPS"</c> and <c>OrderId g</c> with <c>g</c>.
/// </para>
/// </remarks>
public static class ObjectComparison
{
    /// <summary>How deep a graph is followed, so a cycle cannot hang a comparison.</summary>
    public const int MaxDepth = 8;

    /// <summary>One leaf of a compared graph.</summary>
    public sealed record Leaf(string Path, bool Matched, bool Ignored, object? Expected, object? Actual);

    /// <summary>The leaves of <paramref name="value" />, in declaration order.</summary>
    public static IReadOnlyList<(string Path, object? Value)> Leaves(object? value)
    {
        var leaves = new List<(string, object?)>();
        flatten(value, "", 0, leaves);
        return leaves;
    }

    /// <summary>Compare <paramref name="actual" /> with <paramref name="expected" />, leaf by leaf.</summary>
    public static IReadOnlyList<Leaf> Compare(object? actual, object? expected, IReadOnlyCollection<string>? ignored = null)
    {
        ignored ??= Array.Empty<string>();
        var expectedLeaves = Leaves(expected);
        var actualLeaves = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (path, value) in Leaves(actual)) actualLeaves.TryAdd(path, value);

        var results = new List<Leaf>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (path, value) in expectedLeaves)
        {
            seen.Add(path);
            actualLeaves.TryGetValue(path, out var actualValue);
            var isIgnored = ignored.Any(x => covers(x, path));
            results.Add(new Leaf(path, isIgnored || LeafEquals(actualValue, value), isIgnored, value, actualValue));
        }

        // A leaf only the actual graph has: a longer collection, a non-null branch the expectation left null
        foreach (var (path, value) in actualLeaves.Where(x => !seen.Contains(x.Key)))
        {
            var isIgnored = ignored.Any(x => covers(x, path));
            results.Add(new Leaf(path, isIgnored, isIgnored, null, value));
        }

        return results;
    }

    /// <summary>The leaves where the two graphs disagree, as <see cref="ValueDifference"/>s.</summary>
    public static IReadOnlyList<ValueDifference> Differences(object? actual, object? expected,
        IReadOnlyCollection<string>? ignored = null)
        => Compare(actual, expected, ignored)
            .Where(x => !x.Matched)
            .Select(x => new ValueDifference(x.Path, x.Expected, x.Actual))
            .ToList();

    /// <summary>How a leaf value renders in a cell.</summary>
    public static string Format(object? value) => value switch
    {
        null => "NULL",
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    /// <summary>Two leaves agree: same value, or same text where the types differ only in representation.</summary>
    public static bool LeafEquals(object? actual, object? expected)
    {
        actual = Unwrap(actual);
        expected = Unwrap(expected);
        if (actual is null || expected is null) return actual is null && expected is null;
        if (actual.Equals(expected)) return true;
        return string.Equals(Format(actual), Format(expected), StringComparison.Ordinal);
    }

    /// <summary>What an F# option or single-case union holds; anything else as it is.</summary>
    public static object? Unwrap(object? value)
    {
        while (value is not null)
        {
            var type = value.GetType();
            if (FSharpShapes.OptionValueType(type) is not null)
            {
                value = FSharpShapes.IsValueOption(type) && !(bool)type.GetProperty("IsSome")!.GetValue(value)!
                    ? null
                    : type.GetProperty("Value")!.GetValue(value);
                continue;
            }

            if (FSharpShapes.SingleCaseWrapper(type) is not null)
            {
                value = type.GetProperty("Item")?.GetValue(value);
                continue;
            }

            return value;
        }

        return null;
    }

    internal static bool IsLeaf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
               type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
               type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan) ||
               type == typeof(Uri) || type == typeof(Type);
    }

    private static bool covers(string ignored, string path)
        => path == ignored || path.StartsWith(ignored + ".", StringComparison.Ordinal) ||
           path.StartsWith(ignored + "[", StringComparison.Ordinal);

    private static void flatten(object? value, string path, int depth, List<(string, object?)> leaves)
    {
        value = Unwrap(value);

        if (value is null || IsLeaf(value.GetType()) || depth >= MaxDepth)
        {
            leaves.Add((path.Length == 0 ? "value" : path, value));
            return;
        }

        if (value is IEnumerable enumerable)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                flatten(item, $"{path}[{index}]", depth + 1, leaves);
                index++;
            }

            leaves.Add(($"{(path.Length == 0 ? "value" : path)}.Count", index));
            return;
        }

        var properties = value.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.CanRead && x.GetIndexParameters().Length == 0 && x.Name != "EqualityContract")
            .ToArray();

        if (properties.Length == 0)
        {
            // A field-less stub: the type is the whole of what it says
            leaves.Add((path.Length == 0 ? "value" : path, value.GetType().Name));
            return;
        }

        foreach (var property in properties)
        {
            object? child;
            try
            {
                child = property.GetValue(value);
            }
            catch (TargetInvocationException e)
            {
                child = $"<{e.InnerException?.GetType().Name ?? "error"}>";
            }

            flatten(child, path.Length == 0 ? property.Name : $"{path}.{property.Name}", depth + 1, leaves);
        }
    }
}
