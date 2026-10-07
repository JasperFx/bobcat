using System.Linq.Expressions;
using Bobcat.Engine;
using Bobcat.Partial;

namespace Bobcat;

/// <summary>One specified member of a partial object.</summary>
/// <param name="Path">The member path from the object, such as <c>TrackingNumber</c> or <c>Address.City</c>.</param>
/// <param name="Value">The value: typed when it came from code, cell text when it came from a table.</param>
/// <param name="IsText">
/// True when <paramref name="Value"/> is a table cell still to be read with the cell rules
/// (<c>NULL</c>, <c>EMPTY</c>, relative times), false when it is already a value.
/// </param>
public sealed record SpecifiedValue(string Path, object? Value, bool IsText);

/// <summary>
/// An object specified by only some of its members (bobcat#416): the members a specification is
/// about, and nothing else. It can be <em>built</em>, filling the members it does not name, or used to
/// <em>match</em> an actual object on the members it names.
/// </summary>
public interface IPartialObject
{
    /// <summary>The type the members belong to.</summary>
    Type Type { get; }

    /// <summary>The specified members, in the order they were specified.</summary>
    IReadOnlyList<SpecifiedValue> Values { get; }
}

/// <summary>
/// A partial object written in code: <c>Specify&lt;ShipmentConfirmed&gt;().With(x =&gt; x.TrackingNumber, "1Z999")</c>.
/// </summary>
/// <remarks>
/// <b>Refactor friendly by construction.</b> A member is named by an expression rather than a string,
/// so renaming the property renames the specification with it — the long-standing weakness of
/// table-driven tools such as Gherkin, FitNesse and Storyteller. Where a string path is unavoidable,
/// write it with <c>nameof</c>.
/// </remarks>
public sealed class Specified<T> : IPartialObject
{
    private readonly List<SpecifiedValue> _values = new();

    public Type Type => typeof(T);

    public IReadOnlyList<SpecifiedValue> Values => _values;

    /// <summary>Specify <paramref name="member"/> — <c>x =&gt; x.Prop</c>, or a nested <c>x =&gt; x.Address.City</c>.</summary>
    public Specified<T> With<TValue>(Expression<Func<T, TValue>> member, TValue value)
        => with(MemberPaths.Of(member), value);

    /// <summary>
    /// Specify part of a nested object: <c>.With(x =&gt; x.Address, Specify&lt;Address&gt;().With(a =&gt; a.City, "Austin"))</c>
    /// is the same as <c>.With(x =&gt; x.Address.City, "Austin")</c>.
    /// </summary>
    public Specified<T> With<TValue>(Expression<Func<T, TValue>> member, Specified<TValue> nested)
    {
        var prefix = MemberPaths.Of(member);
        foreach (var value in nested.Values) with($"{prefix}.{value.Path}", value.Value);
        return this;
    }

    /// <summary>
    /// Specify the member at <paramref name="path"/> — write it with <c>nameof(Foo.Prop)</c> so it
    /// survives a rename. A string value for a member that is not a string is read with the table
    /// cell rules.
    /// </summary>
    public Specified<T> With(string path, object? value) => with(path, value);

    /// <summary>Build a <typeparamref name="T"/>, filling the members this does not specify.</summary>
    /// <param name="unspecified">The fill policy; <see cref="PredictableValues"/> when omitted.</param>
    public T Build(IUnspecifiedValues? unspecified = null) => (T)PartialObjects.Build(this, unspecified);

    public override string ToString() => PartialObjects.Describe(this);

    private Specified<T> with(string path, object? value)
    {
        // Specifying a member twice means the later value; the order stays where it was first named.
        var index = _values.FindIndex(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        var specified = new SpecifiedValue(path, value, IsText: false);
        if (index >= 0) _values[index] = specified;
        else _values.Add(specified);
        return this;
    }
}

/// <summary>
/// <c>Specify&lt;T&gt;()</c> for code outside a <see cref="Fixture"/> — bring it in with
/// <c>using static Bobcat.Specifications;</c>.
/// </summary>
public static class Specifications
{
    /// <summary>Start a partial <typeparamref name="T"/>; add members with <c>.With(...)</c>.</summary>
    public static Specified<T> Specify<T>() => new();
}

/// <summary>A partial object read from a table: its values are cell text.</summary>
public sealed class TablePartialObject : IPartialObject
{
    public TablePartialObject(Type type, IReadOnlyList<SpecifiedValue> values)
    {
        Type = type;
        Values = values;
    }

    public Type Type { get; }

    public IReadOnlyList<SpecifiedValue> Values { get; }

    /// <summary>Build the object, filling the members this does not specify.</summary>
    public object Build(IUnspecifiedValues? unspecified = null) => PartialObjects.Build(this, unspecified);

    public override string ToString() => PartialObjects.Describe(this);
}

/// <summary>Reading, building and describing partial objects (bobcat#416).</summary>
public static class PartialObjects
{
    /// <summary>
    /// Whether <paramref name="table"/> runs vertically: exactly two columns headed <c>field</c> and
    /// <c>value</c> (ignoring case), one member per row. The convention <c>Given {event} occurred</c>
    /// set, now shared by every table that describes one object.
    /// </summary>
    public static bool IsVertical(StepTable table)
        => table.Headers.Count == 2
           && table.Headers[0].Trim().Equals("field", StringComparison.OrdinalIgnoreCase)
           && table.Headers[1].Trim().Equals("value", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The partial objects a table describes: one per row of a horizontal table, or exactly one from a
    /// vertical table.
    /// </summary>
    /// <remarks>
    /// <b>A blank cell is "not specified".</b> One table may describe objects of several types under the
    /// union of their members, with blanks where a column does not apply. To specify an empty string,
    /// write <c>EMPTY</c>.
    /// </remarks>
    public static IReadOnlyList<TablePartialObject> FromTable(Type type, StepTable table)
    {
        if (!IsVertical(table))
        {
            return table.AsDictionaries().Select(row => FromCells(type, row)).ToList();
        }

        var values = new List<SpecifiedValue>();
        foreach (var row in table.Rows)
        {
            var name = row[0].Trim();
            if (values.Any(x => string.Equals(x.Path, name, StringComparison.OrdinalIgnoreCase)))
                throw new SpecCriticalException(
                    $"The vertical table for {type.Name} names '{name}' twice. Each row is one member, so name each member once.");

            var cell = row.Count > 1 ? row[1] : "";
            if (cell.Trim().Length > 0) values.Add(new SpecifiedValue(name, cell, IsText: true));
        }

        return [new TablePartialObject(type, values)];
    }

    /// <summary>One partial object from a header → cell map. Blank cells are not specified.</summary>
    public static TablePartialObject FromCells(Type type, IReadOnlyDictionary<string, string> cells)
        => new(type, cells
            .Where(cell => cell.Value.Trim().Length > 0)
            .Select(cell => new SpecifiedValue(cell.Key.Trim(), cell.Value, IsText: true))
            .ToList());

    /// <summary>Build <paramref name="partial"/>, filling what it does not specify.</summary>
    /// <param name="unspecified">The fill policy; <see cref="PredictableValues"/> when omitted.</param>
    /// <param name="step">The step text, so a failure names the step to go and fix.</param>
    public static object Build(IPartialObject partial, IUnspecifiedValues? unspecified = null, string? step = null)
        => ObjectConstruction.Build(partial.Type, partial.Values, unspecified ?? PredictableValues.Instance, step);

    /// <summary><c>ShipmentConfirmed(TrackingNumber: 1Z999)</c>: the type and only the members specified.</summary>
    public static string Describe(IPartialObject partial)
        => $"{partial.Type.Name}({DescribeValues(partial)})";

    /// <summary>
    /// <c>TrackingNumber: 1Z999</c>: only the members specified, each value named as describing the
    /// whole object would name it, so a partial and a whole object read alike.
    /// </summary>
    public static string DescribeValues(IPartialObject partial)
        => string.Join(", ", partial.Values.Select(v => $"{v.Path}: {formatValue(partial.Type, v)}"));

    private static string formatValue(Type root, SpecifiedValue value)
    {
        if (value.IsText) return ((string?)value.Value ?? "").Trim();

        ScenarioValues.LearnMember(root, value.Path, value.Value);
        return ScenarioValues.Format(value.Value);
    }
}
