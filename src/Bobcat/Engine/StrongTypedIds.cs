using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace Bobcat.Engine;

/// <summary>
/// A strong-typed identifier (bobcat#433): a type whose public <c>Value</c> is a <see cref="Guid"/>, a string
/// or an integer — <c>record struct OrderId(Guid Value)</c>, a Vogen or a StronglyTypedId type alike. However
/// the type is built, a specification shows it the same way: a Guid-based one by the name its Guid was
/// declared or learned under (<c>theScreeningId</c>), any other as <c>ScreeningId(SCR-104)</c>.
/// </summary>
internal static class StrongTypedIds
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> _values = new();

    /// <summary>Whether <paramref name="type"/> is a strong-typed identifier.</summary>
    public static bool Is(Type type) => valueOf(type) is not null;

    /// <summary>The primitive <paramref name="value"/> wraps, when it is a strong-typed identifier.</summary>
    public static bool TryUnwrap(object value, out object? inner)
    {
        inner = null;
        if (valueOf(value.GetType()) is not { } property) return false;

        inner = property.GetValue(value);
        return true;
    }

    private static PropertyInfo? valueOf(Type type) => _values.GetOrAdd(type, static t =>
    {
        // Generic wrappers (Nullable<T>, KeyValuePair<K, V>, Lazy<T>) have a Value too, and are not ids
        if (t.IsPrimitive || t.IsEnum || t.IsGenericType || t == typeof(string) || t == typeof(Guid)
            || typeof(IEnumerable).IsAssignableFrom(t)) return null;

        var property = t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (property is null || !property.CanRead || property.GetIndexParameters().Length > 0) return null;

        var inner = property.PropertyType;
        return inner == typeof(Guid) || inner == typeof(string) || inner == typeof(int) || inner == typeof(long)
               || inner == typeof(short) || inner == typeof(uint) || inner == typeof(ulong) || inner == typeof(ushort)
            ? property
            : null;
    });
}
