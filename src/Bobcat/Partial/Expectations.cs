using System.Linq.Expressions;
using Bobcat.Partial;
using Bobcat.Runtime;

namespace Bobcat;

/// <summary>
/// An expected value with the members a comparison should show but not judge — a minted id, a
/// timestamp nobody controls (wolverine#4835, moved into Bobcat by bobcat#418). Pass it anywhere an
/// expected object is accepted: <c>Expect.Value(new AppointmentConfirmed(id, default)).Ignoring(x =&gt; x.ConfirmedAt)</c>.
/// </summary>
/// <remarks>
/// The inverse of a partial object: this judges <em>everything except</em> the ignored members, a
/// partial object (<c>Specify&lt;T&gt;()</c>) judges <em>only</em> the members it names. An ignored
/// member is still rendered, as an unjudged value: the minted id is exactly what a reader wants to
/// see when the spec goes red.
/// </remarks>
public interface IExpectedValue
{
    /// <summary>The expected object.</summary>
    object Value { get; }

    /// <summary>Paths (<c>Address.City</c>) shown but not compared. A path also covers everything beneath it.</summary>
    IReadOnlyCollection<string> IgnoredPaths { get; }
}

/// <inheritdoc cref="IExpectedValue" />
public sealed class ExpectedValue<T> : IExpectedValue where T : notnull
{
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);

    public ExpectedValue(T value) => Value = value;

    /// <summary>The expected object.</summary>
    public T Value { get; }

    object IExpectedValue.Value => Value;

    public IReadOnlyCollection<string> IgnoredPaths => _ignored;

    /// <summary>Show <paramref name="member" /> but do not judge it — e.g. <c>x =&gt; x.AssignmentId</c> or <c>x =&gt; x.Address.City</c>.</summary>
    public ExpectedValue<T> Ignoring(Expression<Func<T, object?>> member)
    {
        _ignored.Add(MemberPaths.Of(member));
        return this;
    }

    /// <summary>Show the member at <paramref name="path" /> but do not judge it.</summary>
    public ExpectedValue<T> Ignoring(string path)
    {
        _ignored.Add(path);
        return this;
    }
}

/// <summary>
/// Builds the expectations that are not a plain object: <see cref="Value{T}" /> for a whole one with
/// members to ignore, and the collection forms for a member that holds several things (bobcat#456).
/// </summary>
public static class Expect
{
    /// <summary>Expect <paramref name="value" />, to be refined with <c>.Ignoring(...)</c>.</summary>
    public static ExpectedValue<T> Value<T>(T value) where T : notnull => new(value);

    /// <summary>
    /// Exactly these items, <b>in this order</b> — the one collection form that says something a bare
    /// collection does not, since a bare one is matched in any order.
    /// </summary>
    public static ExpectedItems Exactly(params object[] items) => new(SetMode.Ordered, false, items);

    /// <summary>
    /// Exactly these items, in any order — what a bare collection already means, written out for a
    /// specification that would rather say so.
    /// </summary>
    public static ExpectedItems InAnyOrder(params object[] items) => new(SetMode.AnyOrder, false, items);

    /// <summary>These items, among whatever else the collection holds: nothing is unexpected.</summary>
    public static ExpectedItems Containing(params object[] items) => new(SetMode.Contains, false, items);

    /// <summary>
    /// None of these items — a <see cref="Type" /> for "nothing of that type at all", a partial object
    /// for "none that agrees on the members it names". What else the collection holds is not judged.
    /// </summary>
    public static ExpectedItems NoneOf(params object[] items) => new(SetMode.Contains, true, items);

    /// <summary>The collection is empty.</summary>
    public static ExpectedItems NoItems() => new(SetMode.AnyOrder, false, Array.Empty<object>());

    /// <summary>
    /// The elements of a collection as a table, one row per element, read as
    /// <c>Specify&lt;T&gt;(table)</c> reads one — the <c>VerifySet</c> shape for a child collection whose
    /// rows read better as a grid than as a chain of items. The element type comes from the member.
    /// </summary>
    /// <example>
    /// <code>
    /// Specify&lt;Order&gt;().With(x =&gt; x.Lines, Expect.Rows("""
    ///     | Sku | Quantity |
    ///     | ABC | 2        |
    ///     | DEF | 1        |
    ///     """))
    /// </code>
    /// </example>
    /// <remarks>
    /// <b>Named apart from <see cref="InAnyOrder(object[])" /> and the rest on purpose.</b> A
    /// <see cref="StepTable" /> converts implicitly from a string, so an overload taking one would win
    /// against <c>params object[]</c> for a single string argument: <c>Expect.Containing("vip")</c>, over
    /// a collection of strings, would silently have meant "a table that happens to have no rows" — an
    /// expectation that asserts nothing and passes.
    /// </remarks>
    public static ExpectedItems Rows(StepTable rows, SetMode mode = SetMode.AnyOrder) => new(mode, false, rows);

    /// <summary>No element like any row of <paramref name="rows" /> — <see cref="NoneOf" /> as a table.</summary>
    public static ExpectedItems NoRowsLike(StepTable rows) => new(SetMode.Contains, true, rows);
}
