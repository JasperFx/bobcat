using System.Reflection;
using Bobcat.Runtime;

namespace Bobcat.Partial;

/// <summary>
/// The public instance members a partial object can name on a type: properties (not indexers) and
/// fields, each addressable by its own name or its <see cref="HeaderAttribute"/> title, ignoring case
/// — the same rule <c>PropertyCells</c> and set verification use for a column.
/// </summary>
internal sealed class Members
{
    private static readonly Dictionary<Type, Members> _cache = new();
    private static readonly object _lock = new();

    private readonly List<Member> _all;

    private Members(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Select(p => new Member(p.Name, ColumnNames.Of(p), p.PropertyType, p, null));

        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Select(f => new Member(f.Name, f.GetCustomAttribute<HeaderAttribute>()?.Name ?? f.Name, f.FieldType, null, f));

        _all = properties.Concat(fields).ToList();
    }

    public static Members Of(Type type)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(type, out var members)) _cache[type] = members = new Members(type);
            return members;
        }
    }

    public IEnumerable<Member> Writable => _all.Where(m => m.CanWrite);

    public Member? Find(string name)
        => _all.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
           ?? _all.FirstOrDefault(m => string.Equals(m.Header, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every name a specification could use on <paramref name="type"/>, for an error message.</summary>
    public IEnumerable<string> Names(Type type)
        => _all.Select(m => m.Header)
            .Concat(type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => ColumnNames.Of(p)))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="parameter"/> takes the member named <paramref name="name"/>: by the
    /// parameter's name or title, or by the title of the property it initializes.
    /// </summary>
    public static bool Binds(ParameterInfo parameter, string name, Members members)
    {
        if (string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(ColumnNames.Of(parameter), name, StringComparison.OrdinalIgnoreCase)) return true;

        var member = members.Find(name);
        return member is not null && string.Equals(member.Name, parameter.Name, StringComparison.OrdinalIgnoreCase);
    }

    public static bool AnyConstructorTakes(Type type, string name, Members members)
        => type.GetConstructors().SelectMany(c => c.GetParameters()).Any(p => Binds(p, name, members));
}

internal sealed class Member(string name, string header, Type type, PropertyInfo? property, FieldInfo? fieldInfo)
{
    public string Name { get; } = name;
    public string Header { get; } = header;
    public Type Type { get; } = type;

    /// <summary>A setter or an <c>init</c> accessor (which reflection may call), or a writable field.</summary>
    public bool CanWrite => property?.SetMethod is { IsPublic: true } || fieldInfo is { IsInitOnly: false, IsLiteral: false };

    public bool IsRequired
        => ((MemberInfo?)property ?? fieldInfo)!.CustomAttributes
            .Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute");

    public bool AllowsNull => property is not null ? Nullability.AllowsNull(property) : Nullability.AllowsNull(fieldInfo!);

    public object? Get(object instance) => property is not null ? property.GetValue(instance) : fieldInfo!.GetValue(instance);

    public void Set(object instance, object? value)
    {
        if (property is not null) property.SetValue(instance, value);
        else fieldInfo!.SetValue(instance, value);
    }
}

/// <summary>Whether a member allows null: <see cref="Nullable{T}"/>, or a reference type annotated nullable.</summary>
internal static class Nullability
{
    public static bool AllowsNull(ParameterInfo parameter)
        => allows(parameter.ParameterType, () => new NullabilityInfoContext().Create(parameter).WriteState);

    public static bool AllowsNull(PropertyInfo property)
        => allows(property.PropertyType, () => new NullabilityInfoContext().Create(property).WriteState);

    public static bool AllowsNull(FieldInfo field)
        => allows(field.FieldType, () => new NullabilityInfoContext().Create(field).WriteState);

    private static bool allows(Type type, Func<NullabilityState> state)
    {
        if (type.IsValueType) return Nullable.GetUnderlyingType(type) is not null;

        try
        {
            // Unknown — no annotations, as in F# or nullable-oblivious code — is treated as not
            // allowing null, so an unspecified member is filled rather than left null.
            return state() == NullabilityState.Nullable;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
