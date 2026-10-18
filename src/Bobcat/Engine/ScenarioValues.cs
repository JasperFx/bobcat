using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using Bobcat.Engine.Verification;

namespace Bobcat.Engine;

/// <summary>
/// How a specification shows a VALUE rather than a type name: a record reads
/// <c>OrderStarted(OrderId: #Order, CustomerId: #Customer, Total: 100)</c>, and an identifier reads as
/// a name the scenario gave it, marked with <see cref="AliasMarker"/>, instead of 36 characters of hex.
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

    // Declared names outlive any one scenario: a field initialised when the test class is constructed
    // is declared before its scenario begins. Values are unique (fresh Guids), so one map serves every
    // scenario in the process; the cap only stops a suite that mints millions from growing it forever.
    private static readonly ConcurrentDictionary<object, string> _declared = new();
    private const int DeclaredLimit = 10_000;

    /// <summary>
    /// Name <paramref name="value"/> after the variable, field or property it was assigned to. The
    /// generator calls this for <c>var theAppointmentId = Guid.NewGuid();</c> in a test, so the
    /// specification reads <c>theAppointmentId</c> wherever that value appears. A declared name
    /// beats one learned from a property (<c>AppointmentId</c> → "Appointment"), and loses only to
    /// <see cref="Name"/>.
    /// </summary>
    /// <remarks>
    /// Works outside a scenario too: the name waits until a scenario first meets the value.
    /// </remarks>
    public static T Declare<T>(T value, string name) where T : notnull
    {
        // A Guid-based strong-typed id is declared through its Guid, which is how it is named (bobcat#433)
        object named = StrongTypedIds.TryUnwrap(value, out var inner) && inner is Guid ? inner : value;
        if (!isNameable(named)) return value;

        if (_declared.Count >= DeclaredLimit) _declared.Clear();
        _declared[named] = name;

        if (SpecReport.IsRecording) SpecReport.For<NamedValuesReport>().Assign(named, name, overwrite: true);
        return value;
    }

    internal static string? DeclaredNameOf(object value) => _declared.GetValueOrDefault(value);

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

    /// <summary>
    /// What a name reads as wherever it stands for a value (bobcat#640): <c>#Order</c>, so an alias can
    /// never be mistaken for a type name or a literal — <c>there is no Product #Product</c>. The names
    /// report and <see cref="NamedValuesReport.NameOf"/> keep the bare name.
    /// </summary>
    public const string AliasMarker = "#";

    /// <summary><paramref name="name"/> as an alias reads in a step or a cell: <c>#Order</c>.</summary>
    public static string Alias(string name) => AliasMarker + name;

    /// <summary>One value, as a cell or a step shows it: its name (<c>#Order</c>), a short Guid, or its text.</summary>
    public static string Format(object? value)
    {
        if (value is not null && isNameable(value))
        {
            if (SpecReport.IsRecording && SpecReport.For<NamedValuesReport>().NameOf(value) is { } name) return Alias(name);
            if (value is Guid guid) return guid.ToString()[..8] + "…";
        }

        // bobcat#433: a strong-typed id reads by its Guid's name, or as ScreeningId(SCR-104) -- never as
        // ScreeningId(Value: ...), however the type is built
        if (value is not null && StrongTypedIds.TryUnwrap(value, out var inner))
        {
            if (inner is Guid id && id != Guid.Empty && SpecReport.IsRecording
                && SpecReport.For<NamedValuesReport>().NameOf(id) is { } idName) return Alias(idName);

            return $"{displayName(value.GetType())}({Format(inner)})";
        }

        // bobcat#678: a collection reads as its items, as Describe reads it. Its own ToString is its
        // CLR type name (List`1[System.String]), which is what a partial object's member, its cell
        // and a member check's cell showed -- unless the type wrote one for itself (an F# list)
        if (value is IEnumerable and not string && !rendersItself(value.GetType()))
            return describe(value, 0, learnOnly: false);

        return CheckFormat.Of(value);
    }

    private static readonly ConcurrentDictionary<Type, bool> _rendersItself = new();

    private static bool rendersItself(Type type) => _rendersItself.GetOrAdd(type, static t =>
        t.GetMethod("ToString", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)?.DeclaringType
            is { } declaring && declaring != typeof(object));

    /// <summary>
    /// A duration as a sentence says it (bobcat#682): <c>5 days</c>, <c>1 hour 30 minutes</c>,
    /// <c>250 milliseconds</c> — not <c>5.00:00:00</c>. For step text; a cell compares on the value's own format.
    /// </summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span == TimeSpan.Zero) return "0 seconds";

        var sign = span < TimeSpan.Zero ? "-" : "";
        var magnitude = span.Duration();
        var parts = new List<string>();
        add(parts, magnitude.Days, "day");
        add(parts, magnitude.Hours, "hour");
        add(parts, magnitude.Minutes, "minute");
        add(parts, magnitude.Seconds, "second");
        add(parts, magnitude.Milliseconds, "millisecond");

        // Below a millisecond there is still something to say rather than nothing
        if (parts.Count == 0) return sign + magnitude.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) + " milliseconds";
        return sign + string.Join(" ", parts);

        static void add(List<string> parts, int count, string unit)
        {
            if (count == 0) return;
            parts.Add(count.ToString(CultureInfo.InvariantCulture) + " " + unit + (count == 1 ? "" : "s"));
        }
    }

    /// <summary>
    /// <paramref name="value"/> on one line: <c>Type(Property: value, …)</c>, nested objects the same
    /// way, collections in brackets, a property-less stub as its type name. Learns names as it goes.
    /// </summary>
    public static string Describe(object? value) => describe(value, 0, learnOnly: false);

    /// <summary>
    /// <paramref name="value"/>'s properties without the type around them —
    /// <c>OrderId: Order, Total: 100</c> — for a grid whose other column already names the type. A
    /// scalar or a collection reads as <see cref="Describe"/> does; an object with no properties at all
    /// (<c>record BookReturned;</c>) has nothing to say, so it is empty rather than its type name again
    /// beside the column that already names it (bobcat#618).
    /// </summary>
    public static string DescribeProperties(object? value) => describeProperties(value, omitUnassignedId: false);

    /// <summary>
    /// A document's properties as <see cref="DescribeProperties"/> reads them, leaving out an <c>Id</c> the
    /// store has yet to assign (bobcat#682): a HiLo id is still <c>0</c>, a Guid id <c>Guid.Empty</c>, until
    /// the document is stored, so <c>Id: 0</c> would describe a value the document never has.
    /// </summary>
    public static string DescribeDocument(object? value) => describeProperties(value, omitUnassignedId: true);

    private static string describeProperties(object? value, bool omitUnassignedId)
    {
        if (value is not null && !isScalar(value.GetType()) && value is not IEnumerable
            && readable(value.GetType()).Length == 0) return "";

        var described = describe(value, 0, learnOnly: false, omitUnassignedId);
        if (value is null || isScalar(value.GetType()) || value is IEnumerable) return described;

        if (isAnonymous(value.GetType()) && described.StartsWith("{ ", StringComparison.Ordinal))
            return described[2..^2];

        // Every property left out: nothing to say beside the type that is already named
        if (omitUnassignedId && described == displayName(value.GetType())) return "";

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

    private static string describe(object? value, int depth, bool learnOnly, bool omitUnassignedId = false)
    {
        if (value is null) return "null";

        var type = value.GetType();
        if (value is TimeSpan span) return FormatDuration(span);
        if (isScalar(type)) return value is string s ? quote(s) : Format(value);

        if (value is IEnumerable enumerable)
        {
            var items = enumerable.Cast<object?>().Select(x => describe(x, depth + 1, learnOnly)).ToList();
            return "[" + string.Join(", ", items) + "]";
        }

        var properties = readable(type);
        if (omitUnassignedId && depth == 0)
            properties = properties.Where(x => !(x.Name == "Id" && isUnassignedId(read(x, value)))).ToArray();

        // bobcat#618: an anonymous object has no name worth printing, so it reads as its members
        if (isAnonymous(type) && properties.Length == 0) return "an empty object";
        if (properties.Length == 0 || depth >= MaxDepth) return displayName(type);

        // Learn every name on this object before formatting any of it, so a value is named after the
        // property that introduces it rather than after whichever appears first in the text.
        if (SpecReport.IsRecording)
        {
            var names = SpecReport.For<NamedValuesReport>();
            foreach (var property in properties)
            {
                var child = read(property, value);

                // A Guid-based strong-typed id is named through its Guid, so both read the same
                if (child is not null && StrongTypedIds.TryUnwrap(child, out var inner) && inner is Guid) child = inner;
                if (child is not null && isNameable(child)) names.Assign(child, NameFor(property, type), overwrite: false);
            }
        }

        var anonymous = isAnonymous(type);
        var builder = anonymous ? new StringBuilder("{ ") : new StringBuilder(displayName(type)).Append('(');
        for (var i = 0; i < properties.Length; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.Append(properties[i].Name).Append(": ").Append(describe(read(properties[i], value), depth + 1, learnOnly));
        }

        return builder.Append(anonymous ? " }" : ")").ToString();
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

    private static bool isUnassignedId(object? id) => id switch
    {
        null => true,
        Guid guid => guid == Guid.Empty,
        int number => number == 0,
        long number => number == 0,
        _ => false
    };

    private static string quote(string value) => "\"" + value + "\"";

    /// <summary>
    /// A type as a reader writes it (bobcat#618): <c>List&lt;Account&gt;</c> rather than <c>List`1</c>,
    /// <c>string[]</c>, <c>int?</c>, and an anonymous type as "anonymous object" rather than
    /// <c>&lt;&gt;f__AnonymousType0</c>. For step text; never a key anything compares on.
    /// </summary>
    public static string TypeName(Type type) => displayName(type);

    private static readonly Dictionary<Type, string> keywords = new()
    {
        [typeof(string)] = "string", [typeof(int)] = "int", [typeof(long)] = "long", [typeof(short)] = "short",
        [typeof(byte)] = "byte", [typeof(bool)] = "bool", [typeof(decimal)] = "decimal", [typeof(double)] = "double",
        [typeof(float)] = "float", [typeof(char)] = "char", [typeof(object)] = "object", [typeof(uint)] = "uint",
        [typeof(ulong)] = "ulong", [typeof(ushort)] = "ushort", [typeof(sbyte)] = "sbyte"
    };

    private static string displayName(Type type)
    {
        if (keywords.TryGetValue(type, out var keyword)) return keyword;
        if (type.IsArray) return displayName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (Nullable.GetUnderlyingType(type) is { } underlying) return displayName(underlying) + "?";
        if (isAnonymous(type)) return "anonymous object";
        if (!type.IsGenericType) return type.Name;

        var tick = type.Name.IndexOf('`');
        var name = tick < 0 ? type.Name : type.Name[..tick];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(displayName))}>";
    }

    // The compiler's name for an anonymous type: <>f__AnonymousType0`2 (VB: VB$AnonymousType_0)
    private static bool isAnonymous(Type type)
        => type.Name.Contains("AnonymousType", StringComparison.Ordinal)
           && (type.Name.StartsWith("<>", StringComparison.Ordinal) || type.Name.StartsWith("VB$", StringComparison.Ordinal));

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
               || type == typeof(Uri) || typeof(Type).IsAssignableFrom(type) || StrongTypedIds.Is(type);
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
    public string? NameOf(object value)
    {
        if (_names.TryGetValue(value, out var name)) return name;

        // A value declared before this scenario began (a field) takes its name the first time it is met
        if (ScenarioValues.DeclaredNameOf(value) is not { } declared) return null;

        Assign(value, declared, overwrite: false);
        return _names[value];
    }

    /// <summary>Every name and the value it stands for, in the order they were given.</summary>
    public IReadOnlyDictionary<object, string> Names => _names;

    internal void Assign(object value, string name, bool overwrite)
    {
        // A name learned from a property gives way to the one the test declared it as
        if (!overwrite && ScenarioValues.DeclaredNameOf(value) is { } declared) name = declared;

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
