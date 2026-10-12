using System.Collections;
using Bobcat.Engine;
using Bobcat.Runtime;

namespace Bobcat;

/// <summary>
/// What a specification expects of a collection — a member that holds several things, or a collection
/// compared on its own (bobcat#456). The items are expectations in their own right, so an element may
/// be a whole object, a <c>Specify&lt;T&gt;()</c>, an <see cref="Expect.Value{T}" /> or a bare
/// <see cref="Type" />.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unordered is the default, and strict ordering is opt in</b> (decided 2026-10-12): a bare
/// collection means <em>exactly these items, in any order</em>, and <see cref="Expect.Exactly" /> is
/// how a specification says that order is part of the claim. Most collection members have an order the
/// system happens to produce rather than one the specification is about, and asserting it by accident
/// reads as every element being wrong the first time anything reorders.
/// </para>
/// <para>
/// Matching is <see cref="ObjectSetVerification" />'s, the same rules a top-level set verification
/// uses — a maximum matching rather than position by position, a near match reported as a FAIL with the
/// members that disagreed rather than as a MISSING beside an EXTRA.
/// </para>
/// </remarks>
public interface IExpectedItems
{
    /// <summary>How the items are matched. <see cref="SetMode.Contains" /> allows items nobody expected.</summary>
    SetMode Mode { get; }

    /// <summary>Whether these items are forbidden rather than expected — <see cref="Expect.NoneOf" />.</summary>
    bool Forbidden { get; }

    /// <summary>
    /// The expectations, one per element. <paramref name="elementType" /> is the collection's element
    /// type where the caller knows it, so a table-written expectation need not repeat it; null when
    /// nothing knows it, which only a table form minds.
    /// </summary>
    IReadOnlyList<object> ItemsFor(Type? elementType);
}

/// <inheritdoc cref="IExpectedItems" />
public sealed class ExpectedItems : IExpectedItems
{
    private readonly IReadOnlyList<object>? _items;
    private readonly StepTable? _table;
    private readonly Type? _elementType;

    internal ExpectedItems(SetMode mode, bool forbidden, IReadOnlyList<object> items)
    {
        Mode = mode;
        Forbidden = forbidden;
        _items = items;
    }

    internal ExpectedItems(SetMode mode, bool forbidden, StepTable table, Type? elementType = null)
    {
        Mode = mode;
        Forbidden = forbidden;
        _table = table;
        _elementType = elementType;
    }

    public SetMode Mode { get; }

    public bool Forbidden { get; }

    public IReadOnlyList<object> ItemsFor(Type? elementType)
    {
        if (_items is not null) return _items;

        var type = _elementType ?? elementType
            ?? throw new SpecCriticalException(
                "A collection written as a table needs to know what its elements are, and nothing here does. "
                + "Specify the member with .With(x => x.Items, ...) so the element type comes from the member, "
                + "or write the items out instead of a table.");

        return PartialObjects.FromTable(type, _table!).Cast<object>().ToList();
    }

    /// <summary>The same expectation, with <paramref name="elementType" /> settled — what a member knows and a table does not.</summary>
    public ExpectedItems ForElement(Type elementType)
        => _items is not null ? this : new ExpectedItems(Mode, Forbidden, _table!, elementType);

    /// <summary>The element type of <paramref name="collection" />, or null for one that declares none.</summary>
    public static Type? ElementTypeOf(Type? collection)
    {
        if (collection is null || collection == typeof(string)) return null;
        if (collection.IsArray) return collection.GetElementType();

        return collection.GetInterfaces().Append(collection)
            .FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    /// <summary>
    /// The items of <paramref name="value" /> when it is a collection to be matched as one, else null.
    /// A string is not a collection of characters here, and a dictionary is compared by key rather than
    /// as a set. A null element becomes <see cref="NullItem" />, so one can be matched like any other.
    /// </summary>
    public static IReadOnlyList<object>? ItemsOf(object? value)
        => value is IEnumerable enumerable && value is not string && !ObjectComparison.IsDictionary(value)
            ? enumerable.Cast<object?>().Select(x => x ?? NullItem.Instance).ToList()
            : null;

    /// <summary>The items of <paramref name="expectation" />, with nulls as <see cref="NullItem" />.</summary>
    public static IReadOnlyList<object> ItemsOf(IExpectedItems expectation, Type? elementType)
        => expectation.ItemsFor(elementType).Select(x => x ?? NullItem.Instance).ToList();

    public override string ToString() => Describe(this, null);

    /// <summary>How an items expectation reads: <c>exactly Line(Sku: "A"), in order</c>.</summary>
    public static string Describe(IExpectedItems expectation, Type? elementType)
    {
        IReadOnlyList<object> items;
        try
        {
            items = expectation.ItemsFor(elementType);
        }
        catch (SpecCriticalException)
        {
            return "a table of items";
        }

        if (items.Count == 0) return expectation.Forbidden ? "nothing forbidden" : "no items";

        var described = string.Join(", ", items.Select(PartialMatching.DescribeItem));
        if (expectation.Forbidden) return $"none of {described}";

        return expectation.Mode switch
        {
            SetMode.Ordered => $"exactly {described}, in order",
            SetMode.AnyOrder => $"exactly {described}, in any order",
            _ => $"{described}, among others"
        };
    }
}

/// <summary>
/// A null element of a collection, so that one can be paired and reported like any other item: a set
/// verification reads an item's runtime type, which a bare null has none of.
/// </summary>
public sealed class NullItem
{
    public static readonly NullItem Instance = new();

    private NullItem()
    {
    }

    public override string ToString() => "NULL";
}
