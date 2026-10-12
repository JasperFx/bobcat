using System.Collections;
using System.Collections.Concurrent;
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
/// A graph comes apart into its <b>leaves</b> — <c>Address.City</c>, <c>Lines[0].Sku</c> — and each
/// leaf is compared and rendered as its own cell, so a deep graph is still a cell table rather than
/// one cell holding a document diff.
/// </para>
/// <para>
/// <b>Indexes appear in what this reports, never in what a spec writes.</b> A leaf path such as
/// <c>Lines[0].Sku</c> names where two whole graphs differ; a specification naming a member
/// (<c>PropertyCells</c>, a partial object) may not index into a collection, because that question
/// belongs to a set verification. The two rules answer different questions and do not conflict.
/// </para>
/// <para>
/// <b>A collection is a set and a dictionary is keyed</b> (bobcat#456). A collection member is matched
/// by <see cref="ObjectSetVerification" />'s rules — exactly these items, <em>in any order</em>, with
/// an item nobody expected EXTRA and an expectation nothing satisfied MISSING; order is part of the
/// claim only where a specification says so with <c>Expect.Exactly</c>. Comparing position by position
/// read a reordered collection as every element being wrong, which is the failure that type exists to
/// avoid. A dictionary's leaves are <c>Headers[accept]</c>, keyed by the entry, so two dictionaries
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
    /// <param name="Note">Why, where the two values alone do not say it — MISSING, EXTRA, out of order.</param>
    public sealed record Leaf(string Path, bool Matched, bool Ignored, object? Expected, object? Actual,
        string? Note = null);

    /// <summary>The leaves of <paramref name="value" />, in declaration order.</summary>
    public static IReadOnlyList<(string Path, object? Value)> Leaves(object? value)
    {
        var leaves = new List<(string, object?)>();
        flatten(value, "", 0, leaves);
        return leaves;
    }

    /// <summary>Compare <paramref name="actual" /> with <paramref name="expected" />, leaf by leaf.</summary>
    public static IReadOnlyList<Leaf> Compare(object? actual, object? expected,
        IReadOnlyCollection<string>? ignored = null)
        => CompareAt("", actual, expected, ignored);

    /// <summary>
    /// <see cref="Compare" />, with every path under <paramref name="path" /> — for comparing one
    /// member of a larger object, so a difference reads <c>Lines[0].Quantity</c> and not <c>[0].Quantity</c>.
    /// </summary>
    public static IReadOnlyList<Leaf> CompareAt(string path, object? actual, object? expected,
        IReadOnlyCollection<string>? ignored = null)
    {
        var leaves = new List<Leaf>();

        // One ancestor set for the whole comparison, however deep it re-enters — a collection's items
        // are compared through PartialMatching, which comes back here, so a set the top call owned
        // privately would be a fresh one per element and a cycle through a collection would not
        // terminate.
        var owned = _ancestors is null;
        _ancestors ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        try
        {
            compare(actual, expected, path, 0, ignored ?? Array.Empty<string>(), leaves);
        }
        finally
        {
            if (owned) _ancestors = null;
        }

        return leaves;
    }

    /// <summary>The leaves where the two graphs disagree, as <see cref="ValueDifference"/>s.</summary>
    public static IReadOnlyList<ValueDifference> Differences(object? actual, object? expected,
        IReadOnlyCollection<string>? ignored = null)
        => DifferencesAt("", actual, expected, ignored);

    /// <inheritdoc cref="CompareAt" />
    public static IReadOnlyList<ValueDifference> DifferencesAt(string path, object? actual, object? expected,
        IReadOnlyCollection<string>? ignored = null)
        => CompareAt(path, actual, expected, ignored)
            .Where(x => !x.Matched)
            .Select(x => new ValueDifference(x.Path, x.Expected, x.Actual, x.Note))
            .ToList();

    /// <summary>How a leaf value renders in a cell.</summary>
    public static string Format(object? value) => value switch
    {
        null => "NULL",
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),

        // Not a leaf, and nothing of its own to say: a collection, or an object the depth cap stopped
        // at. Its own ToString is the wrong renderer twice over — a list gives its type name, where a
        // reader wants its items, and a record's generated ToString walks the graph, so a cycle
        // recurses until the stack goes. Describe renders items and stops at its own depth.
        _ when !IsLeaf(value.GetType()) && !rendersItself(value.GetType()) => ScenarioValues.Describe(value),

        _ => value.ToString() ?? ""
    };

    private static readonly ConcurrentDictionary<Type, bool> _rendersItself = new();

    /// <summary>
    /// Whether <paramref name="type"/> has a <c>ToString</c> somebody wrote for it, which is then how
    /// it wants to be read. A <b>record's</b> does not count: that one is generated, walks the members
    /// the graph walk is already covering, and recurses forever on a cycle — <c>PrintMembers</c> is how
    /// it gives itself away.
    /// </summary>
    private static bool rendersItself(Type type) => _rendersItself.GetOrAdd(type, static t =>
    {
        var toString = t.GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        if (toString is null || toString.DeclaringType == typeof(object)) return false;

        return t.GetMethod("PrintMembers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is null;
    });

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

    /// <summary>
    /// Whether <paramref name="type" /> is one value rather than a graph to walk — so it has no members
    /// to disagree about, and nothing inside it to name.
    /// </summary>
    public static bool IsLeaf(Type type)
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

    [ThreadStatic] private static HashSet<object>? _ancestors;

    private static void compare(object? actual, object? expected, string path, int depth,
        IReadOnlyCollection<string> ignored, List<Leaf> leaves)
    {
        actual = Unwrap(actual);
        expected = Unwrap(expected);

        var name = path.Length == 0 ? "value" : path;
        var isIgnored = ignored.Any(x => covers(x, name));

        // A collection the specification has an opinion about: order, "among others", or forbidden
        if (expected is IExpectedItems items)
        {
            compareItems(actual, items, name, depth, isIgnored, leaves);
            return;
        }

        // Either side a leaf, either side absent, or as deep as this goes: one judged value
        if (expected is null || actual is null || IsLeaf(expected.GetType()) || IsLeaf(actual.GetType())
            || depth >= MaxDepth)
        {
            leaves.Add(new Leaf(name, isIgnored || LeafEquals(actual, expected), isIgnored, expected, actual));
            return;
        }

        if (!_ancestors!.Add(expected))
        {
            leaves.Add(new Leaf(name, true, isIgnored, cycleOf(expected), cycleOf(expected)));
            return;
        }

        try
        {
            if (IsDictionary(expected) && IsDictionary(actual))
            {
                compareEntries(actual, expected, name, depth, ignored, leaves);
                return;
            }

            // A collection with nothing said about it is "exactly these items, in any order"
            // (bobcat#456): most collection members have an order the system happens to produce
            // rather than one the specification is about.
            if (ExpectedItems.ItemsOf(expected) is { } elements)
            {
                compareItems(actual, new ExpectedItems(SetMode.AnyOrder, false, elements), name, depth, isIgnored,
                    leaves);
                return;
            }

            // Two different types, which is one disagreement and not a disagreement about every
            // member each type happens not to share. Checked AFTER the collection arms, where a
            // List<T> and a T[] are the same claim; checked at all because the top level already
            // requires the exact type, and a silent type claim at depth is the odd one out.
            if (actual.GetType() != expected.GetType())
            {
                leaves.Add(new Leaf(name, isIgnored, isIgnored, expected, actual,
                    isIgnored ? null : $"Expected a {expected.GetType().Name}, was a {actual.GetType().Name}"));
                return;
            }

            var properties = expected.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(x => x.CanRead && x.GetIndexParameters().Length == 0 && x.Name != "EqualityContract")
                .ToArray();

            if (properties.Length == 0)
            {
                // A field-less stub: the type is the whole of what it says, and the types agree
                leaves.Add(new Leaf(name, true, isIgnored, expected.GetType().Name, actual.GetType().Name));
                return;
            }

            foreach (var property in properties)
            {
                compare(read(property, actual), read(property, expected),
                    path.Length == 0 ? property.Name : $"{path}.{property.Name}", depth + 1, ignored, leaves);
            }
        }
        finally
        {
            _ancestors!.Remove(expected);
        }
    }

    // A collection member, matched by ObjectSetVerification's rules rather than position by position
    private static void compareItems(object? actual, IExpectedItems items, string name, int depth, bool isIgnored,
        List<Leaf> leaves)
    {
        var actualItems = ExpectedItems.ItemsOf(actual);
        var expectedItems = ExpectedItems.ItemsOf(items, ExpectedItems.ElementTypeOf(actual?.GetType()));

        if (actualItems is null)
        {
            // Not a collection at all — a null member, or a member of some other shape entirely
            leaves.Add(new Leaf(name, isIgnored, isIgnored, ExpectedItems.Describe(items, null), actual,
                isIgnored ? null : "Expected a collection"));
            return;
        }

        if (isIgnored)
        {
            leaves.Add(new Leaf(name, true, true, null, actual));
            return;
        }

        if (items.Forbidden)
        {
            forbid(actualItems, expectedItems, name, leaves);
            return;
        }

        if (expectedItems.Count == 0 && actualItems.Count == 0)
        {
            // Said once, so an empty collection still appears in the report rather than silently
            leaves.Add(new Leaf(name, true, false, actual, actual));
            return;
        }

        var pairing = ObjectSetVerification.Pair(actualItems, expectedItems,
            (item, i) => elementDifferences(item, expectedItems[i], depth + 1), items.Mode);

        for (var i = 0; i < expectedItems.Count; i++)
        {
            var item = expectedItems[i];
            var type = PartialMatching.ExpectedType(item);
            var typeName = type.Name;
            var position = pairing.Pairs[i];

            if (position < 0)
            {
                // Named by the member and not by an index: it was not found ANYWHERE, so no position
                // is the one it should have been at
                leaves.Add(new Leaf(name, false, false, PartialMatching.DescribeItem(item), null,
                    IsLeaf(type) ? "MISSING: nothing like this" : $"MISSING: no {typeName} like this"));
                continue;
            }

            if (pairing.OutOfOrder[i] >= 0)
            {
                leaves.Add(new Leaf($"{name}[{position}]", false, false, PartialMatching.DescribeItem(item),
                    PartialMatching.DescribeItem(actualItems[position]),
                    ObjectSetVerification.OutOfOrderNote(typeName, position, pairing.OutOfOrder[i])));
                continue;
            }

            if (pairing.Differences[i] is { Count: > 0 } differences)
            {
                foreach (var difference in differences)
                {
                    leaves.Add(new Leaf(pathOf(name, position, difference.Path), false, false, difference.Expected,
                        difference.Actual, difference.Note));
                }

                continue;
            }

            leaves.Add(new Leaf($"{name}[{position}]", true, false, PartialMatching.DescribeItem(item),
                PartialMatching.DescribeActual(actualItems[position], item)));
        }

        // "Among others" allows what nobody expected, so it is neither judged nor shown
        if (items.Mode == SetMode.Contains) return;

        for (var j = 0; j < actualItems.Count; j++)
        {
            if (pairing.Owner[j] >= 0) continue;

            leaves.Add(new Leaf($"{name}[{j}]", false, false, null, PartialMatching.DescribeItem(actualItems[j]),
                IsLeaf(actualItems[j].GetType())
                    ? "EXTRA: nothing expected this"
                    : $"EXTRA: nothing expected a {actualItems[j].GetType().Name} here"));
        }
    }

    // Expect.NoneOf: one leaf per forbidden item, and what else the collection holds is not judged
    private static void forbid(IReadOnlyList<object> actualItems, IReadOnlyList<object> forbidden, string name,
        List<Leaf> leaves)
    {
        foreach (var item in forbidden)
        {
            var type = PartialMatching.ExpectedType(item);
            var position = -1;
            for (var j = 0; j < actualItems.Count && position < 0; j++)
            {
                if (item is Type ? actualItems[j].GetType() == type : PartialMatching.Matches(actualItems[j], item))
                    position = j;
            }

            var expected = $"no {PartialMatching.DescribeItem(item)}";
            if (position < 0)
            {
                leaves.Add(new Leaf(name, true, false, expected, expected));
                continue;
            }

            leaves.Add(new Leaf($"{name}[{position}]", false, false, expected,
                PartialMatching.DescribeItem(actualItems[position]),
                $"PRESENT: a {type.Name} like this was not expected"));
        }
    }

    // A dictionary is keyed, so a key one side lacks is that key and nothing else
    private static void compareEntries(object actual, object expected, string name, int depth,
        IReadOnlyCollection<string> ignored, List<Leaf> leaves)
    {
        var actualEntries = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in asDictionary(actual)!) actualEntries.TryAdd(ScenarioValues.Format(key), value);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, value) in asDictionary(expected)!)
        {
            var text = ScenarioValues.Format(key);
            seen.Add(text);

            if (!actualEntries.TryGetValue(text, out var actualValue))
            {
                leaves.Add(new Leaf($"{name}[{text}]", false, false, value, null,
                    $"MISSING: no entry under '{text}'"));
                continue;
            }

            compare(actualValue, value, $"{name}[{text}]", depth + 1, ignored, leaves);
        }

        foreach (var (text, value) in actualEntries.Where(x => !seen.Contains(x.Key)))
        {
            leaves.Add(new Leaf($"{name}[{text}]", false, false, null, value,
                $"EXTRA: no entry expected under '{text}'"));
        }
    }

    /// <summary>Whether <paramref name="value" /> is a dictionary, and so compared by key.</summary>
    public static bool IsDictionary(object? value) => value is not null && asDictionary(value) is not null;

    // An element's own comparison: whatever kind of expectation it is, but sharing this comparison's
    // depth so a graph that cycles through a collection terminates
    private static IReadOnlyList<ValueDifference> elementDifferences(object actual, object expected, int depth)
    {
        if (expected is IPartialObject or Type or IExpectedItems) return PartialMatching.Differences(actual, expected);

        var leaves = new List<Leaf>();
        compare(actual, expected is IExpectedValue value ? value.Value : expected, "", depth,
            (expected as IExpectedValue)?.IgnoredPaths ?? Array.Empty<string>(), leaves);

        return leaves
            .Where(x => !x.Matched)
            .Select(x => new ValueDifference(x.Path, x.Expected, x.Actual, x.Note))
            .ToList();
    }

    // "value" is what a comparison of two bare values calls its one leaf; inside a collection the
    // element's own path is the member and the index, so there is nothing to append
    private static string pathOf(string name, int position, string path)
        => path is "value" or "" ? $"{name}[{position}]" : $"{name}[{position}].{path}";

    private static string cycleOf(object value) => $"<cycle back to {value.GetType().Name}>";

    private static object? read(PropertyInfo property, object owner)
    {
        try
        {
            return property.GetValue(owner);
        }
        catch (TargetInvocationException e)
        {
            return $"<{e.InnerException?.GetType().Name ?? "error"}>";
        }
    }

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
