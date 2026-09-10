using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Bobcat;
using Bobcat.Engine;

namespace Bobcat.CritterStack;

/// <summary>
/// Builds a command or event object from a Gherkin table row at runtime — the piece the shipped
/// grammars need that the compile-time entity binder (<c>[MartenEntities]</c>) does not cover,
/// because a grammar's event/command type is named in the step text (<c>{command}</c>,
/// <c>{event}</c>) and its columns are read as constructor arguments per row. Records land on their
/// primary constructor; a settable-property object is the fallback. Cells convert with the same
/// rules a Gherkin literal uses everywhere else in Bobcat.
/// </summary>
/// <remarks>
/// <para>
/// <b>Public because <c>[IncludeGrammars]</c> is the designed extension point</b> (issue #272).
/// Any custom grammar module that declares a <see cref="StepTable"/> parameter needs exactly this
/// conversion, and while it was <c>internal</c> every one of them had to hand-roll a poorer copy —
/// each with its own type coercion, its own treatment of a blank cell, and its own message when a
/// column matches nothing. That is precisely the divergence issue #241 spent effort removing from
/// the shipped grammars, reintroduced once per consumer.
/// </para>
/// <para>
/// Being public also means the shipped conventions are what a custom grammar inherits for free:
/// the partial-row rule, the empty-cell rule, and the "a column matching no parameter is refused
/// by name" message all come along, so a hand-written grammar and a shipped one fail the same way
/// over the same table.
/// </para>
/// </remarks>
public static class RecordBuilding
{
    /// <summary>
    /// Construct one instance of <paramref name="type"/> from a header → cell map. Prefers the public
    /// constructor whose parameters the columns can all supply (records-friendly), then a
    /// parameterless constructor with settable-property assignment.
    /// </summary>
    /// <param name="partial">
    /// Arranging history rather than performing an act (issue #241). A <c>Given</c> names the fields
    /// the behaviour under test depends on — "given a home check was proposed to this owner" — and
    /// the rest of a six-field event is not part of the scenario; demanding a column for each makes
    /// the table say things the scenario does not mean. Unsupplied parameters take
    /// <c>default(T)</c>.
    /// <para>
    /// An act is deliberately NOT partial: a command's fields <em>are</em> the scenario's input, so
    /// a missing one is a spec that tests something other than what it says.
    /// </para>
    /// </param>
    public static object Build(Type type, IReadOnlyDictionary<string, string> cells, string? step = null,
        bool partial = false)
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

        // Nothing binds completely, but this is a Given: take the constructor the columns reach
        // furthest into and default the rest (issue #241).
        ctor ??= partial
            ? type.GetConstructors()
                .Where(c => c.GetParameters().Length > 0)
                .OrderByDescending(c => c.GetParameters().Count(p => cells.ContainsKey(p.Name!)))
                .ThenByDescending(c => c.GetParameters().Length)
                .FirstOrDefault()
            : null;

        if (ctor != null)
        {
            refuseUnmatchedColumns(type, cells, ctor, step);

            var args = ctor.GetParameters()
                .Select(p => cells.TryGetValue(p.Name!, out var raw)
                    ? GherkinValue.Convert(raw, p.ParameterType)
                    : unsupplied(p))
                .ToArray();
            return ctor.Invoke(args);
        }

        var parameterless = type.GetConstructor(Type.EmptyTypes);
        if (parameterless != null)
        {
            refuseUnmatchedColumns(type, cells, ctor: null, step);
            var instance = parameterless.Invoke([]);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.SetMethod == null) continue;
                if (!cells.TryGetValue(property.Name, out var raw)) continue;
                property.SetValue(instance, GherkinValue.Convert(raw, property.PropertyType));
            }

            return instance;
        }

        // Name the step and the fields it did not get. The reader's next move is to add columns,
        // and the message they used to get was a bare NRE from inside the fixture (issue #233).
        var wanted = type.GetConstructors()
            .Where(c => c.GetParameters().Length > 0)
            .OrderByDescending(c => c.GetParameters().Length)
            .Select(c => string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")))
            .FirstOrDefault();

        throw new SpecCriticalException(
            (step is null ? "" : $"'{step}': ")
            + $"cannot build '{type.Name}' from the columns [{string.Join(", ", cells.Keys)}]"
            + (wanted is null ? ". It has no public constructor to build it with." : $" — it needs ({wanted}).")
            + " Give the step a one-row table naming those columns.");
    }

    /// <summary>
    /// What a parameter no column supplied is worth. Three cases, and reflection reports them
    /// differently enough that collapsing them has bitten before:
    /// <list type="bullet">
    /// <item>an explicit C# default (<c>= 3</c>) — the value the author chose;</item>
    /// <item><c>= default</c> on a value type — reported as a null <c>DefaultValue</c>, so the
    /// actual <c>default(T)</c> has to be materialized;</item>
    /// <item>no default at all — reported as <see cref="DBNull"/>, and reachable only in partial
    /// mode (issue #241), where <c>default(T)</c> is exactly what "the scenario does not mention
    /// it" means.</item>
    /// </list>
    /// </summary>
    private static object? unsupplied(ParameterInfo parameter)
    {
        var fallback = parameter.ParameterType.IsValueType
            ? Activator.CreateInstance(parameter.ParameterType)
            : null;

        if (!parameter.HasDefaultValue) return fallback;
        return parameter.DefaultValue is null or DBNull ? fallback : parameter.DefaultValue;
    }

    /// <summary>
    /// A column matching nothing on the target is the case actually worth failing on — a typo, or a
    /// field that has been renamed since the spec was written. Relaxing the missing-column rule
    /// (issue #241) removes the accident that used to catch those, so name them here instead.
    /// </summary>
    /// <remarks>
    /// An EMPTY unmatched cell is ignored on purpose: one <c>Given events for …</c> table may carry
    /// rows of several event types, and its header is then the union of their fields, with blanks
    /// where a column does not apply to a row. A typo always arrives with a value in it.
    /// </remarks>
    private static void refuseUnmatchedColumns(Type type, IReadOnlyDictionary<string, string> cells,
        ConstructorInfo? ctor, string? step)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in ctor?.GetParameters() ?? []) known.Add(parameter.Name!);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            known.Add(property.Name);
        }

        var unmatched = cells
            .Where(cell => !known.Contains(cell.Key) && !string.IsNullOrWhiteSpace(cell.Value))
            .Select(cell => cell.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (unmatched.Count == 0) return;

        throw new SpecCriticalException(
            (step is null ? "" : $"'{step}': ")
            + $"the column{(unmatched.Count == 1 ? "" : "s")} [{string.Join(", ", unmatched)}] "
            + $"match nothing on '{type.Name}', which has ({string.Join(", ", known.OrderBy(x => x, StringComparer.Ordinal))}). "
            + "Check the spelling, or the field may have been renamed since this spec was written.");
    }

    /// <summary>Build one object per <see cref="StepTable"/> row, all of the same <paramref name="type"/>.</summary>
    /// <param name="step">
    /// The step text, so a failure names the step the reader has to go and fix rather than only the
    /// type. Optional, but a grammar that has it should pass it.
    /// </param>
    /// <param name="partial">
    /// Arranging rather than acting — see <see cref="Build"/>. Applies to every row.
    /// </param>
    public static IReadOnlyList<object> BuildAll(Type type, StepTable table, string? step = null,
        bool partial = false)
        => table.AsDictionaries().Select(row => Build(type, row, step, partial)).ToList();
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
