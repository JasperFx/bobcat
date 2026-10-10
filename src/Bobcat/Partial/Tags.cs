namespace Bobcat;

/// <summary>
/// An expectation that also names the tags its object must carry (JasperFx/wolverine#4865): for an event,
/// the Dynamic Consistency Boundary tags it was appended with, each a strong-typed identifier.
/// </summary>
/// <remarks>
/// <b>An exact, unordered match.</b> When an expectation names tags, the actual object's tags must be
/// exactly those -- none missing, none extra -- in any order. An expectation that names no tags says
/// nothing about them, and they are neither judged nor shown.
/// </remarks>
public interface ITaggedExpectation
{
    /// <summary>The tags expected, or empty when the expectation says nothing about tags.</summary>
    IReadOnlyList<object> ExpectedTags { get; }
}

/// <summary>
/// A whole object expected along with its tags: <c>Tagged(new SeatReserved(theScreeningId, "4C"), theScreeningId)</c>.
/// Compared like any whole object, structurally, or like the <see cref="IExpectedValue" /> it wraps.
/// </summary>
public sealed class TaggedValue : IExpectedValue, ITaggedExpectation
{
    internal TaggedValue(object value, IReadOnlyCollection<string> ignoredPaths, IReadOnlyList<object> tags)
    {
        Value = value;
        IgnoredPaths = ignoredPaths;
        ExpectedTags = tags;
    }

    public object Value { get; }

    public IReadOnlyCollection<string> IgnoredPaths { get; }

    public IReadOnlyList<object> ExpectedTags { get; }

    public override string ToString() => $"{PartialMatching.DescribeExpected(this)}";

    /// <summary>
    /// <paramref name="value" /> expected with exactly <paramref name="tags" />. A partial object says its
    /// tags itself, with <c>Specify&lt;T&gt;().Tagged(...)</c>.
    /// </summary>
    internal static TaggedValue For(object value, object[] tags)
    {
        ArgumentNullException.ThrowIfNull(value);
        var checkedTags = Tagging.Check(tags);

        return value switch
        {
            IPartialObject partial => throw new ArgumentException(
                $"A partial {partial.Type.Name} names its tags itself: Specify<{partial.Type.Name}>().Tagged(...).", nameof(value)),
            ITaggedExpectation => throw new ArgumentException("The expectation already names its tags.", nameof(value)),
            IExpectedValue expected => new TaggedValue(expected.Value, expected.IgnoredPaths, checkedTags),
            _ => new TaggedValue(value, Array.Empty<string>(), checkedTags)
        };
    }
}

/// <summary>How tags are judged and shown (JasperFx/wolverine#4865).</summary>
public static class Tagging
{
    /// <summary>The tags <paramref name="expected" /> names, or none when it says nothing about tags.</summary>
    public static IReadOnlyList<object> ExpectedTagsOf(object expected)
        => expected is ITaggedExpectation tagged ? tagged.ExpectedTags : Array.Empty<object>();

    /// <summary>
    /// Whether <paramref name="actual" /> is exactly <paramref name="expected" />, in any order: each expected
    /// tag matched to one actual tag of the same type and value, with none left over on either side.
    /// </summary>
    public static bool AreExactly(IReadOnlyList<object>? actual, IReadOnlyList<object> expected)
    {
        var remaining = (actual ?? Array.Empty<object>()).ToList();
        if (remaining.Count != expected.Count) return false;

        foreach (var tag in expected)
        {
            var index = remaining.FindIndex(x => x.GetType() == tag.GetType() && Equals(x, tag));
            if (index < 0) return false;
            remaining.RemoveAt(index);
        }

        return true;
    }

    /// <summary>Tags as one value: <c>[theScreeningId, ScreeningId(SCR-104)]</c>, or <c>[]</c>.</summary>
    public static string Describe(IEnumerable<object>? tags)
        => "[" + string.Join(", ", (tags ?? Array.Empty<object>()).Select(Engine.ScenarioValues.Format)) + "]";

    internal static IReadOnlyList<object> Check(object[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Length == 0) throw new ArgumentException("Name at least one tag.", nameof(tags));
        if (tags.Any(x => x is null)) throw new ArgumentException("A tag cannot be null.", nameof(tags));
        return tags.ToArray();
    }
}
