using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Bobcat;

/// <summary>
/// Storyteller-style declared defaults (bobcat#421): "this is the value of <c>Order.Currency</c>
/// when a spec doesn't say". Consulted before <see cref="Fallback"/>, which is
/// <see cref="PredictableValues"/> unless another policy is given.
/// </summary>
/// <remarks>
/// <para>
/// A default belongs to a member of a type wherever that type is built, at the root or nested:
/// <c>For&lt;Address&gt;(x =&gt; x.Country, "US")</c> fills the <c>Country</c> of every
/// <c>Address</c>, including an order's <c>ShippingAddress.Country</c>. A path through several
/// members (<c>x =&gt; x.ShippingAddress.Country</c>) declares the default on the last member's
/// type, which is the same thing said from further out.
/// </para>
/// <para>
/// A value is used as given, so it should be immutable. Give a factory when each build needs its
/// own instance, such as a list, or a fresh value each time.
/// </para>
/// <para>
/// Register once for every spec in a project through <see cref="PartialObjects.UnspecifiedValues"/>,
/// or per spec through its <c>UnspecifiedValues</c>.
/// </para>
/// </remarks>
public sealed class DeclaredDefaults : IUnspecifiedValues
{
    private readonly ConcurrentDictionary<(Type Type, string Member), Func<object?>> _defaults = new();

    /// <param name="fallback">What fills a member no default is declared for; <see cref="PredictableValues"/> when omitted.</param>
    public DeclaredDefaults(IUnspecifiedValues? fallback = null)
    {
        Fallback = fallback ?? PredictableValues.Instance;
    }

    /// <summary>What fills a member no default is declared for.</summary>
    public IUnspecifiedValues Fallback { get; }

    /// <summary>When <typeparamref name="T"/>'s <paramref name="member"/> is unspecified, it is <paramref name="value"/>.</summary>
    public DeclaredDefaults For<T, TMember>(Expression<Func<T, TMember>> member, TMember value)
        => add(member, () => value);

    /// <summary>When <typeparamref name="T"/>'s <paramref name="member"/> is unspecified, it is a fresh value from <paramref name="value"/>.</summary>
    public DeclaredDefaults For<T, TMember>(Expression<Func<T, TMember>> member, Func<TMember> value)
        => add(member, () => value());

    /// <summary>The members a default is declared for, as <c>Type.Member</c>.</summary>
    public IReadOnlyList<string> Declared => _defaults.Keys.Select(x => $"{x.Type.Name}.{x.Member}").Order().ToList();

    public bool TryValueFor(UnspecifiedMember member, out object? value)
    {
        var name = member.Path[(member.Path.LastIndexOf('.') + 1)..];
        if (_defaults.TryGetValue((member.DeclaringType, name), out var factory))
        {
            value = factory();
            return true;
        }

        return Fallback.TryValueFor(member, out value);
    }

    private DeclaredDefaults add<T, TMember>(Expression<Func<T, TMember>> member, Func<object?> factory)
    {
        var accessed = lastMember(member.Body)
                       ?? throw new ArgumentException(
                           $"A declared default names a member of {typeof(T).Name}, as in x => x.Name; '{member.Body}' does not.",
                           nameof(member));

        var declaring = accessed.Expression?.Type ?? typeof(T);
        _defaults[(declaring, accessed.Member.Name)] = factory;
        return this;
    }

    private static MemberExpression? lastMember(Expression body) => body switch
    {
        MemberExpression { Member: PropertyInfo or FieldInfo } member => member,

        // A value-type member boxed to fit, as in x => (object)x.Count
        UnaryExpression { NodeType: ExpressionType.Convert } convert => lastMember(convert.Operand),
        _ => null
    };
}
