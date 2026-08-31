using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Bobcat;

namespace Bobcat.CritterStack;

/// <summary>
/// Builds a command or event object from a Gherkin table row at runtime — the piece the shipped
/// grammars need that the compile-time entity binder (<c>[MartenEntities]</c>) does not cover,
/// because a grammar's event/command type is named in the step text (<c>{command}</c>,
/// <c>{event}</c>) and its columns are read as constructor arguments per row. Records land on their
/// primary constructor; a settable-property object is the fallback. Cells convert with the same
/// rules a Gherkin literal uses everywhere else in Bobcat.
/// </summary>
internal static class RecordBuilding
{
    /// <summary>
    /// Construct one instance of <paramref name="type"/> from a header → cell map. Prefers the public
    /// constructor whose parameters the columns can all supply (records-friendly), then a
    /// parameterless constructor with settable-property assignment.
    /// </summary>
    public static object Build(Type type, IReadOnlyDictionary<string, string> cells)
    {
        // A parameter with a C# default does not need a column (bobcat#177 dogfood finding):
        // real commands routinely carry optional trailing parameters (a nullable Session, a
        // defaulted lease), and demanding a column for each made every table say "null" for
        // things the author never mentions in code either. Prefer the constructor binding the
        // MOST columns, so a fuller table still wins over a shorter overload.
        var ctor = type.GetConstructors()
            .Where(c => c.GetParameters().Length > 0)
            .Where(c => c.GetParameters().All(p => cells.ContainsKey(p.Name!) || p.HasDefaultValue))
            .OrderByDescending(c => c.GetParameters().Count(p => cells.ContainsKey(p.Name!)))
            .ThenByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (ctor != null)
        {
            var args = ctor.GetParameters()
                .Select(p => cells.TryGetValue(p.Name!, out var raw)
                    ? GherkinValue.Convert(raw, p.ParameterType)
                    // An optional value-type parameter declared `= default` reports a null
                    // DefaultValue through reflection; materialize the actual default(T).
                    : p.DefaultValue ?? (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null))
                .ToArray();
            return ctor.Invoke(args);
        }

        var parameterless = type.GetConstructor(Type.EmptyTypes);
        if (parameterless != null)
        {
            var instance = parameterless.Invoke([]);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.SetMethod == null) continue;
                if (!cells.TryGetValue(property.Name, out var raw)) continue;
                property.SetValue(instance, GherkinValue.Convert(raw, property.PropertyType));
            }

            return instance;
        }

        throw new InvalidOperationException(
            $"Cannot build '{type.FullName}' from the columns [{string.Join(", ", cells.Keys)}]. No public constructor's " +
            "parameters are all supplied by columns, and there is no parameterless constructor to set properties on.");
    }

    /// <summary>Build one object per <see cref="StepTable"/> row, all of the same <paramref name="type"/>.</summary>
    public static IReadOnlyList<object> BuildAll(Type type, StepTable table)
        => table.AsDictionaries().Select(row => Build(type, row)).ToList();
}

/// <summary>
/// The one runtime type-name lookup the grammars need: an event type named by a column value
/// (<c>Given events for {aggregate}</c> with an <c>Event</c> column). Searches loaded assemblies by
/// simple name, preferring a hint assembly (the aggregate's). Cached. The compile-time captures
/// (<c>{command}</c>, <c>{event}</c>) never come here — the generator already resolved those to
/// <c>typeof(...)</c>; this is only for a type named in table <i>data</i>.
/// </summary>
internal static class EventTypeResolver
{
    private static readonly ConcurrentDictionary<string, Type> _cache = new(StringComparer.Ordinal);

    public static Type Resolve(string name, Assembly? hint = null)
    {
        var key = (hint?.FullName ?? "") + "|" + name;
        return _cache.GetOrAdd(key, _ => resolve(name, hint));
    }

    private static Type resolve(string name, Assembly? hint)
    {
        // A namespace-qualified name resolves directly; a simple name matches by Type.Name.
        bool Matches(Type t) => t.FullName == name || t.Name == name;

        if (hint != null)
        {
            var inHint = safeTypes(hint).FirstOrDefault(Matches);
            if (inHint != null) return inHint;
        }

        var matches = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .SelectMany(safeTypes)
            .Where(Matches)
            .Distinct()
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"No type named '{name}' is loaded, so the grammar cannot construct that event. Is the project that " +
                "declares it referenced by the spec assembly?"),
            _ => throw new InvalidOperationException(
                $"'{name}' is ambiguous — {matches.Count} loaded types have that name ({string.Join(", ", matches.Select(t => t.FullName))}). " +
                "Use the namespace-qualified name in the table.")
        };
    }

    private static IEnumerable<Type> safeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t != null)!;
        }
    }
}

/// <summary>
/// Converts a Gherkin cell string to a target type — the runtime twin of the generator's
/// compile-time literal conversion, for the grammars' reflective record building. Handles the
/// primitives, string, enums, Guid, decimal and the date/time types, plus their nullable forms.
/// </summary>
internal static class GherkinValue
{
    public static object? Convert(string raw, Type target)
    {
        var underlying = Nullable.GetUnderlyingType(target);
        if (underlying != null)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            target = underlying;
        }

        if (target == typeof(string)) return raw;
        if (target.IsEnum) return Enum.Parse(target, raw, ignoreCase: true);
        if (target == typeof(Guid)) return Guid.Parse(raw);
        if (target == typeof(bool)) return bool.Parse(raw);
        if (target == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
        if (target == typeof(long)) return long.Parse(raw, CultureInfo.InvariantCulture);
        if (target == typeof(short)) return short.Parse(raw, CultureInfo.InvariantCulture);
        if (target == typeof(byte)) return byte.Parse(raw, CultureInfo.InvariantCulture);
        if (target == typeof(double)) return double.Parse(raw, CultureInfo.InvariantCulture);
        if (target == typeof(float)) return float.Parse(raw, CultureInfo.InvariantCulture);
        if (target == typeof(decimal)) return decimal.Parse(raw, CultureInfo.InvariantCulture);
        if (target == typeof(DateTime)) return DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (target == typeof(DateTimeOffset)) return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (target == typeof(DateOnly)) return DateOnly.Parse(raw, CultureInfo.InvariantCulture);
        if (target == typeof(TimeOnly)) return TimeOnly.Parse(raw, CultureInfo.InvariantCulture);
        if (target == typeof(TimeSpan)) return TimeSpan.Parse(raw, CultureInfo.InvariantCulture);

        return System.Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);
    }
}
