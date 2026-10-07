using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Bobcat.Engine.Verification;

namespace Bobcat.Engine;

/// <summary>
/// How a specification shows a VALUE rather than a type name: a record reads
/// <c>OrderStarted(OrderId: Order, CustomerId: Customer, Total: 100)</c>, and an identifier reads as
/// a name the scenario gave it instead of 36 characters of hex.
/// </summary>
/// <remarks>
/// <para>
/// <b>Names come from property names.</b> The first time a <see cref="Guid"/> is seen under a
/// property, it is named after that property, minus a trailing <c>Id</c>: the value of
/// <c>CustomerId</c> becomes <c>Customer</c>, and every later appearance of the same value — in
/// another event, a message, a step's text — reads <c>Customer</c> too. A bare <c>Id</c> takes the
/// declaring type's name. A different value wanting a name already taken gets a number:
/// <c>Order</c>, then <c>Order2</c>. <see cref="Name"/> overrides all of it for the rare value worth
/// naming by hand.
/// </para>
/// <para>
/// <b>Per scenario.</b> The names live in the scenario's <see cref="NamedValuesReport"/>, which is
/// also how the full values stay findable: when the scenario fails, the report lists each name
/// beside the value it stands for. Outside a scenario nothing is named and a Guid is shortened to
/// its first eight characters.
/// </para>
/// </remarks>
public static class ScenarioValues
{
    /// <summary>How deep <see cref="Describe"/> follows nested objects before printing a type name.</summary>
    public const int MaxDepth = 3;

    /// <summary>
    /// Past this length a description is too long to read inline, and a caller should show the value
    /// as a table of its properties instead.
    /// </summary>
    public const int InlineLimit = 120;

    /// <summary>Name <paramref name="value"/> <paramref name="name"/> for the rest of the scenario.</summary>
    public static void Name(object value, string name)
    {
        if (!SpecReport.IsRecording) return;
        SpecReport.For<NamedValuesReport>().Assign(value, name, overwrite: true);
    }

    /// <summary>
    /// Learn names from <paramref name="value"/>'s properties without describing it — for a value the
    /// scenario will mention later by identity alone, such as a stream id.
    /// </summary>
    public static void Learn(object? value) => describe(value, 0, learnOnly: true);

    /// <summary>
    /// Name <paramref name="id"/> after <paramref name="owner"/> unless it already has a name — an
    /// aggregate's stream id, named after the aggregate.
    /// </summary>
    public static void Learn(object? id, string owner)
    {
        if (id is null || !SpecReport.IsRecording || !isNameable(id)) return;
        SpecReport.For<NamedValuesReport>().Assign(id, owner, overwrite: false);
    }

    /// <summary>One value, as a cell or a step shows it: its name, a short Guid, or its text.</summary>
    public static string Format(object? value)
    {
        if (value is not null && isNameable(value))
        {
            if (SpecReport.IsRecording && SpecReport.For<NamedValuesReport>().NameOf(value) is { } name) return name;
            if (value is Guid guid) return guid.ToString()[..8] + "…";
        }

        return CheckFormat.Of(value);
    }

    /// <summary>
    /// <paramref name="value"/> on one line: <c>Type(Property: value, …)</c>, nested objects the same
    /// way, collections in brackets, a property-less stub as its type name. Learns names as it goes.
    /// </summary>
    public static string Describe(object? value) => describe(value, 0, learnOnly: false);

    /// <summary>
    /// <paramref name="value"/>'s properties without the type around them —
    /// <c>OrderId: Order, Total: 100</c> — for a grid whose other column already names the type. A
    /// value with no properties to show reads as <see cref="Describe"/> does.
    /// </summary>
    public static string DescribeProperties(object? value)
    {
        var described = describe(value, 0, learnOnly: false);
        if (value is null || isScalar(value.GetType()) || value is IEnumerable) return described;

        var prefix = displayName(value.GetType()) + "(";
        return described.StartsWith(prefix, StringComparison.Ordinal) && described.EndsWith(')')
            ? described[prefix.Length..^1]
            : described;
    }

    /// <summary>Several values described, comma-separated, or "nothing".</summary>
    public static string DescribeAll(IEnumerable<object?> values)
    {
        var described = values.Select(Describe).ToList();
        return described.Count == 0 ? "nothing" : string.Join(", ", described);
    }

    /// <summary>Whether <paramref name="value"/> prints as a single token rather than as its properties.</summary>
    public static bool IsScalar(object? value) => value is null || isScalar(value.GetType());

    private static string describe(object? value, int depth, bool learnOnly)
    {
        if (value is null) return "null";

        var type = value.GetType();
        if (isScalar(type)) return value is string s ? quote(s) : Format(value);

        if (value is IEnumerable enumerable)
        {
            var items = enumerable.Cast<object?>().Select(x => describe(x, depth + 1, learnOnly)).ToList();
            return "[" + string.Join(", ", items) + "]";
        }

        var properties = readable(type);
        if (properties.Length == 0 || depth >= MaxDepth) return displayName(type);

        // Learn every name on this object before formatting any of it, so a value is named after the
        // property that introduces it rather than after whichever appears first in the text.
        if (SpecReport.IsRecording)
        {
            var names = SpecReport.For<NamedValuesReport>();
            foreach (var property in properties)
            {
                var child = read(property, value);
                if (child is not null && isNameable(child)) names.Assign(child, NameFor(property, type), overwrite: false);
            }
        }

        var builder = new StringBuilder(displayName(type)).Append('(');
        for (var i = 0; i < properties.Length; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.Append(properties[i].Name).Append(": ").Append(describe(read(properties[i], value), depth + 1, learnOnly));
        }

        return builder.Append(')').ToString();
    }

    /// <summary>
    /// Learn a name for a partial object's specified value (bobcat#416), as describing the whole object
    /// would have: <c>AppointmentId</c> names its Guid "Appointment". <paramref name="path"/> is dotted
    /// from <paramref name="root"/>; a path that does not resolve to a property learns nothing.
    /// </summary>
    internal static void LearnMember(Type root, string path, object? value)
    {
        if (!SpecReport.IsRecording || value is null || !isNameable(value)) return;

        var declaring = root;
        PropertyInfo? property = null;
        foreach (var segment in path.Split('.'))
        {
            if (property is not null) declaring = property.PropertyType;
            property = readable(declaring).FirstOrDefault(x => x.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (property is null) return;
        }

        SpecReport.For<NamedValuesReport>().Assign(value, NameFor(property!, declaring), overwrite: false);
    }

    /// <summary>The name a value takes from the property it is first seen under.</summary>
    internal static string NameFor(PropertyInfo property, Type declaring)
    {
        var name = property.Name;
        if (name.Equals("Id", StringComparison.Ordinal)) return displayName(declaring);
        if (name.Length > 2 && name.EndsWith("Id", StringComparison.Ordinal)) return name[..^2];
        return name;
    }

    private static bool isNameable(object value) => value is Guid g && g != Guid.Empty;

    private static string quote(string value) => "\"" + value + "\"";

    private static string displayName(Type type)
    {
        if (!type.IsGenericType) return type.Name;
        var name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(displayName))}>";
    }

    private static object? read(PropertyInfo property, object owner)
    {
        try
        {
            return property.GetValue(owner);
        }
        catch (TargetInvocationException e)
        {
            return $"<{e.InnerException?.GetType().Name ?? "error"}>";
        }
    }

    private static PropertyInfo[] readable(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.CanRead && x.GetIndexParameters().Length == 0 && x.Name != "EqualityContract")
            .ToArray();

    private static bool isScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
               || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
               || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan)
               || type == typeof(Uri) || typeof(Type).IsAssignableFrom(type);
    }
}

/// <summary>
/// The names a scenario gave its values (<see cref="ScenarioValues"/>), and — when the scenario
/// fails — the table that says what each name stands for.
/// </summary>
public sealed class NamedValuesReport : TableReport
{
    private readonly Dictionary<object, string> _names = new();
    private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

    public override string Title => "Named values";

    /// <summary>The name <paramref name="value"/> was given, or null.</summary>
    public string? NameOf(object value) => _names.GetValueOrDefault(value);

    /// <summary>Every name and the value it stands for, in the order they were given.</summary>
    public IReadOnlyDictionary<object, string> Names => _names;

    internal void Assign(object value, string name, bool overwrite)
    {
        if (_names.TryGetValue(value, out var existing))
        {
            if (!overwrite || existing == name) return;
            _taken.Remove(existing);
        }

        var unique = name;
        for (var n = 2; _taken.Contains(unique); n++) unique = name + n.ToString(CultureInfo.InvariantCulture);

        _names[value] = unique;
        _taken.Add(unique);
        Row(("name", unique), ("value", value));
    }
}
