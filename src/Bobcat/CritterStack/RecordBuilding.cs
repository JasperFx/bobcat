using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Bobcat;
using Bobcat.Engine;

namespace Bobcat.CritterStack;

/// <summary>
/// Builds a command or event object from a Gherkin table row at runtime — the piece the shipped
/// grammars need that a compile-time entity binder (<c>[EfCoreEntities]</c>) does not cover,
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
    /// Construct one instance of <paramref name="type"/> from a header → cell map, through the
    /// partial-object engine (bobcat#419): the constructor binding the most columns, then settable or
    /// <c>init</c> members; members no column names take <c>default(T)</c> (<see cref="DefaultValues"/>),
    /// as they have since issue #241, until bobcat#421 settles what should fill them.
    /// </summary>
    /// <param name="partial">
    /// Kept for compatibility, and no longer changes anything. Issue #241 made an arranged <c>Given</c>
    /// partial and kept the <c>When</c> act strict; bobcat#419 made the act partial too — the
    /// preference #241 itself stated — so every build is partial. What still fails, by name, is a
    /// column matching nothing on the type: the typo or the rename worth catching.
    /// </param>
    /// <remarks>
    /// A blank cell is "not specified", so one <c>Given events for …</c> table can carry rows of
    /// several event types under the union of their fields. To specify an empty string, write
    /// <c>EMPTY</c>.
    /// </remarks>
    public static object Build(Type type, IReadOnlyDictionary<string, string> cells, string? step = null,
        bool partial = false)
        => PartialObjects.Build(PartialObjects.FromCells(type, cells), DefaultValues.Instance, step);

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
/// Converts a Gherkin cell string to a target type, for the grammars' reflective record building.
/// </summary>
/// <remarks>
/// Delegates to <see cref="Bobcat.Runtime.CellValues"/>, which is the one runtime authority on what a
/// cell means. It used to be a third copy of the rules beside the generator's literal emission and the
/// expected-side checkers, and the copies had drifted: this one could not read <c>TODAY+2</c> or
/// <c>NULL</c>, so a table that supplied a date and a table that asserted one disagreed about what the
/// same word meant.
/// </remarks>
internal static class GherkinValue
{
    public static object? Convert(string raw, Type target) => Bobcat.Runtime.CellValues.Read(raw, target);
}
