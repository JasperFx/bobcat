using System.Linq.Expressions;
using Bobcat.Partial;

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

/// <summary>Builds an <see cref="ExpectedValue{T}" />.</summary>
public static class Expect
{
    /// <summary>Expect <paramref name="value" />, to be refined with <c>.Ignoring(...)</c>.</summary>
    public static ExpectedValue<T> Value<T>(T value) where T : notnull => new(value);
}
