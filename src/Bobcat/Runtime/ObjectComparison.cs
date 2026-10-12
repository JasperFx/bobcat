using System.Collections;
using System.Globalization;
using System.Reflection;
using Bobcat.Engine;
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
/// <b>A collection is positional and a dictionary is keyed</b> (bobcat#456). A list's leaves are
/// <c>Lines[0].Sku</c>, so the comparison is ordered and exact — which is what a <c>List&lt;T&gt;</c>
/// means in C#. A dictionary's are <c>Headers[accept]</c>, keyed by the entry, so two dictionaries
/// holding the same entries agree whatever order they enumerate in and a key one side lacks reads as
/// that one key.
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

        // Not a leaf: a collection, or an object the depth cap stopped at. Its own ToString is the
        // wrong renderer twice over — a list gives its type name, where a reader wants its items,
        // and a record's generated ToString walks the graph, so a cycle recurses until the stack
        // goes. Describe renders items, stops at its own depth and never calls a user ToString.
        _ when !IsLeaf(value.GetType()) => ScenarioValues.Describe(value),

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
               type == typeof(Uri) ||
               // A Type's runtime type is RuntimeType, never typeof(Type): followed as a graph, its
               // properties fan out past any depth limit, so every reflection object is a leaf
               typeof(MemberInfo).IsAssignableFrom(type) || typeof(Assembly).IsAssignableFrom(type) ||
               typeof(Module).IsAssignableFrom(type);
    }

    private static bool covers(string ignored, string path)
        => path == ignored || path.StartsWith(ignored + ".", StringComparison.Ordinal) ||
           path.StartsWith(ignored + "[", StringComparison.Ordinal);

    private static void flatten(object? value, string path, int depth, List<(string, object?)> leaves,
        HashSet<object>? ancestors = null)
    {
        value = Unwrap(value);
        var name = path.Length == 0 ? "value" : path;

        if (value is null || IsLeaf(value.GetType()) || depth >= MaxDepth)
        {
            leaves.Add((name, value));
            return;
        }

        // A graph that refers back to something already on this path. The depth cap alone used to
        // "handle" it, by emitting the object itself as the leaf at level 8 — which then recursed
        // through the record's own ToString when anything formatted it. Said once, the same text on
        // both sides, so two cyclic graphs still agree.
        ancestors ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (!ancestors.Add(value))
        {
            leaves.Add((name, $"<cycle back to {value.GetType().Name}>"));
            return;
        }

        try
        {
            if (asDictionary(value) is { } entries)
            {
                // Keyed, not positional: the key is the path, so two dictionaries holding the same
                // entries agree whatever order they enumerate in, and a key one side lacks reads as
                // that key rather than as every later entry being wrong.
                var count = 0;
                foreach (var (key, item) in entries)
                {
                    flatten(item, $"{name}[{ScenarioValues.Format(key)}]", depth + 1, leaves, ancestors);
                    count++;
                }

                leaves.Add(($"{name}.Count", count));
                return;
            }

            if (value is IEnumerable enumerable)
            {
                var index = 0;
                foreach (var item in enumerable)
                {
                    flatten(item, $"{path}[{index}]", depth + 1, leaves, ancestors);
                    index++;
                }

                leaves.Add(($"{name}.Count", index));
                return;
            }

            var properties = value.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(x => x.CanRead && x.GetIndexParameters().Length == 0 && x.Name != "EqualityContract")
                .ToArray();

            if (properties.Length == 0)
            {
                // A field-less stub: the type is the whole of what it says
                leaves.Add((name, value.GetType().Name));
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

                flatten(child, path.Length == 0 ? property.Name : $"{path}.{property.Name}", depth + 1, leaves,
                    ancestors);
            }
        }
        finally
        {
            // Off the path again: a graph may hold the same child twice without that being a cycle
            ancestors.Remove(value);
        }
    }

    /// <summary>
    /// The entries of a dictionary, or null for anything else. Deliberately <b>only</b> a dictionary
    /// and not every <c>IEnumerable&lt;KeyValuePair&lt;,&gt;&gt;</c>: a <c>List&lt;KeyValuePair&lt;,&gt;&gt;</c>
    /// is an ordered list that may hold one key twice, and reading it by key would both lose its order
    /// and silently drop a duplicate's leaves.
    /// </summary>
    private static IEnumerable<(object Key, object? Value)>? asDictionary(object value)
    {
        // Enumerated as IEnumerable a Dictionary<K, V> yields KeyValuePair, so the entries have to
        // come from IDictionary's own enumerator
        if (value is IDictionary dictionary) return entriesOf(dictionary);

        var readOnly = value.GetType().GetInterfaces().FirstOrDefault(x =>
            x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));
        if (readOnly is null) return null;

        return pairsOf((IEnumerable)value);

        static IEnumerable<(object, object?)> entriesOf(IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary) yield return (entry.Key, entry.Value);
        }

        // An IReadOnlyDictionary that is no IDictionary enumerates as KeyValuePair<,>, read by property
        static IEnumerable<(object, object?)> pairsOf(IEnumerable pairs)
        {
            PropertyInfo? key = null, item = null;
            foreach (var pair in pairs)
            {
                key ??= pair.GetType().GetProperty("Key")!;
                item ??= pair.GetType().GetProperty("Value")!;
                yield return (key.GetValue(pair)!, item.GetValue(pair));
            }
        }
    }
}
