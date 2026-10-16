using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Bobcat.Engine;
using Bobcat.Engine.Verification;

namespace Bobcat.Runtime;

/// <summary>
/// Reads one written cell — a cell of a table literal, a text-expected value — as a value
/// of a target type. The one runtime authority on what a cell means.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is one place.</b> There were three: the generator's compile-time literal emission, a
/// private converter behind <see cref="Bobcat.CritterStack.RecordBuilding"/>, and the
/// <see cref="IValueChecker{T}"/> family on the expected side. Each knew a slightly different set of
/// types and none of them agreed about tokens: <c>TODAY+2</c> worked when a specification
/// <i>asserted</i> a date and was a build error when it <i>supplied</i> one, which is the same word
/// meaning two things in one document.
/// </para>
/// <para>
/// <b>The expressions.</b> Beyond a plain literal a cell may say <c>NULL</c> or <c>EMPTY</c>
/// (<see cref="CellTokens"/>), or a relative time — <c>TODAY</c>, <c>TODAY+2</c>,
/// <c>NOW - 30 minutes</c> (<see cref="RelativeTimeResolver"/>, against
/// <see cref="BobcatClock.Current"/> so a spec that fakes the clock gets its own answer). To mean the
/// literal text of a token, wrap it in double quotes, exactly as on the expected side.
/// </para>
/// <para>
/// <b>Relative times resolve at run time, never at build time.</b> The generator emits a call to this
/// rather than a computed date, because "today" is a fact about the run: a build cached overnight
/// would otherwise hand every later run yesterday's date, and nothing in the report would say so.
/// </para>
/// <para>
/// <b>Domain types (bobcat#542).</b> A type none of the built-in readers know is read, in order,
/// through a conversion <see cref="Register{T}"/> was given for it, then its <see cref="IParsable{TSelf}"/>
/// <c>Parse</c>, then a <see cref="TypeConverter"/> that converts from a string, then a public
/// constructor taking one string. A registration wins over every reader, built-in or conventional, so a
/// suite can override how a type it does not own is written. The tokens keep their meaning whatever is
/// registered: <c>NULL</c>, <c>EMPTY</c>, a quoted literal and a relative time are decided before any
/// conversion runs.
/// </para>
/// </remarks>
public static class CellValues
{
    private static readonly ConcurrentDictionary<Type, Func<string, object?>> _registered = new();
    private static readonly ConcurrentDictionary<Type, Func<string, object?>?> _conventions = new();

    /// <summary>
    /// Reads every cell of <typeparamref name="T"/> through <paramref name="read"/>, for a type the
    /// suite does not own or one whose own parsing is not how a specification writes it. Global to the
    /// process; registering a type again replaces the earlier conversion.
    /// </summary>
    /// <remarks>
    /// The conversion sees the cell's text trimmed, and the inner text of a quoted cell. It never sees
    /// <c>NULL</c>, <c>EMPTY</c> or a relative time against a date type — those are read before it is
    /// asked. Anything it throws becomes a <see cref="BadCellException"/> naming the cell.
    /// </remarks>
    public static void Register<T>(Func<string, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _registered[typeof(T)] = text => read(text);
    }

    /// <summary>Removes a conversion <see cref="Register{T}"/> was given, if there was one.</summary>
    public static bool Unregister<T>() => _registered.TryRemove(typeof(T), out _);

    /// <summary>The cell as a <typeparamref name="T"/>. The form generated code calls.</summary>
    public static T Read<T>(string text) => (T)Read(text, typeof(T))!;

    /// <summary>
    /// The cell as <paramref name="target"/>, or null where the cell says so and the target allows it.
    /// </summary>
    /// <exception cref="BadCellException">
    /// The cell cannot be read as that type — named rather than left as a <see cref="FormatException"/>
    /// from somewhere inside a parse, and outside the <c>Spec*</c> tier so one bad row does not take
    /// the scenario with it.
    /// </exception>
    public static object? Read(string text, Type target)
    {
        var underlying = Nullable.GetUnderlyingType(target);
        var nullable = underlying != null;
        if (nullable) target = underlying!;

        var trimmed = text.Trim();

        // Quoted means "the literal text", so a cell really can say the word NULL.
        if (isQuoted(trimmed))
        {
            var literal = trimmed.Substring(1, trimmed.Length - 2);
            return target == typeof(string) ? literal : convert(literal, target, text);
        }

        if (string.Equals(trimmed, CellTokens.Null, StringComparison.OrdinalIgnoreCase))
        {
            if (nullable || !target.IsValueType) return null;
            throw cannotRead(text, target, $"{target.Name} cannot be null");
        }

        if (string.Equals(trimmed, CellTokens.Empty, StringComparison.OrdinalIgnoreCase))
        {
            if (target == typeof(string)) return "";
            if (nullable || !target.IsValueType) return null;
            throw cannotRead(text, target, $"{target.Name} has no empty value");
        }

        // An empty cell against a nullable is the one way a table says "no value"; against a string
        // it IS the value.
        if (trimmed.Length == 0)
        {
            if (target == typeof(string)) return text;
            if (nullable) return null;
        }

        if (IsRelativeTime(trimmed) && isTemporal(target))
        {
            if (!RelativeTimeResolver.TryResolve(trimmed, BobcatClock.Current, out var resolved, out _))
                throw cannotRead(text, target, $"'{trimmed}' is not a relative time this can resolve");

            return fromDateTime(resolved, target);
        }

        return convert(trimmed, target, text);
    }

    /// <summary>
    /// Whether a cell reads as a relative time rather than a literal — <c>TODAY</c>, <c>NOW</c> and
    /// their offsets.
    /// </summary>
    /// <remarks>
    /// Deliberately a shape test rather than a resolve: the generator has to answer the same question
    /// at compile time, in an assembly that cannot reference this one, and the two must not disagree
    /// about whether a cell is an expression. A cell that looks relative and cannot be resolved is a
    /// run-time failure naming it, which is the safe direction — see
    /// <c>RelativeTimeAgreementTests</c>.
    /// </remarks>
    public static bool IsRelativeTime(string text)
    {
        var upper = text.Trim().ToUpperInvariant();

        return upper == "TODAY" || upper == "NOW"
               || upper.StartsWith("TODAY+", StringComparison.Ordinal)
               || upper.StartsWith("TODAY-", StringComparison.Ordinal)
               || upper.StartsWith("TODAY ", StringComparison.Ordinal)
               || upper.StartsWith("NOW+", StringComparison.Ordinal)
               || upper.StartsWith("NOW-", StringComparison.Ordinal)
               || upper.StartsWith("NOW ", StringComparison.Ordinal);
    }

    /// <summary>The types a relative time can be read as.</summary>
    public static bool IsTemporalType(Type type)
        => isTemporal(Nullable.GetUnderlyingType(type) ?? type);

    /// <summary>
    /// Always throws. The signature returns <typeparamref name="T"/> so the call sits in the
    /// argument position the value would have occupied.
    /// </summary>
    /// <remarks>
    /// A value the generator cannot read is <b>BOBCAT030</b> at build time, and the feature is
    /// suppressed — so nothing here should ever run. It exists because the alternative floor was worse:
    /// before BOBCAT030 the generator emitted the cell's text verbatim, and a specification that said
    /// <c>Given a quantity of oops</c> failed the build with <c>CS0103: the name 'oops' does not exist</c>
    /// in a generated file the author cannot open. If a binding path the validator does not walk ever
    /// reaches here, the author gets a sentence naming their cell instead.
    /// </remarks>
    public static T Unreadable<T>(string parameter, string value, string problem)
        => throw new SpecCriticalException(
            $"The value '{value}' for '{parameter}' could not be read"
            + (problem.Length > 0 ? $": {problem}" : "."));

    private static bool isTemporal(Type target)
        => target == typeof(DateTime) || target == typeof(DateTimeOffset)
                                      || target == typeof(DateOnly) || target == typeof(TimeOnly);

    private static object fromDateTime(DateTime resolved, Type target)
    {
        if (target == typeof(DateTime)) return resolved;
        if (target == typeof(DateTimeOffset)) return new DateTimeOffset(resolved, TimeSpan.Zero);
        if (target == typeof(DateOnly)) return DateOnly.FromDateTime(resolved);
        return TimeOnly.FromDateTime(resolved);
    }

    private static bool isQuoted(string text)
        => text.Length >= 2 && text.StartsWith("\"", StringComparison.Ordinal)
                            && text.EndsWith("\"", StringComparison.Ordinal);

    private static object? convert(string raw, Type target, string original)
    {
        if (_registered.TryGetValue(target, out var registered)) return invoke(registered, raw, target, original);

        try
        {
            if (target == typeof(string)) return raw;

            if (target.IsEnum)
            {
                // Named, like BOBCAT030 names them at build time. .NET's own message is
                // "Requested value 'Shortstop' was not found", which tells the author nothing about
                // what they could have written instead.
                if (Enum.TryParse(target, raw, ignoreCase: true, out var member)) return member;

                throw cannotRead(original, target,
                    $"'{raw}' is not one of {target.Name}'s values ({string.Join(", ", Enum.GetNames(target))})");
            }

            if (target == typeof(Guid)) return Guid.Parse(raw);
            if (target == typeof(bool)) return bool.Parse(raw);
            if (target == typeof(char)) return char.Parse(raw);
            if (target == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(long)) return long.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(uint)) return uint.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(ulong)) return ulong.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(short)) return short.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(ushort)) return ushort.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(byte)) return byte.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(sbyte)) return sbyte.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(double)) return double.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(float)) return float.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(decimal)) return decimal.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(DateTime))
                return DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (target == typeof(DateTimeOffset))
                return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (target == typeof(DateOnly)) return DateOnly.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(TimeOnly)) return TimeOnly.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(TimeSpan)) return TimeSpan.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(Uri)) return new Uri(raw, UriKind.RelativeOrAbsolute);
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException
                                     or InvalidCastException)
        {
            throw cannotRead(original, target, e.Message);
        }

        if (_conventions.GetOrAdd(target, conventionFor) is { } convention)
            return invoke(convention, raw, target, original);

        if (typeof(IConvertible).IsAssignableFrom(target))
        {
            try
            {
                return System.Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException
                                         or InvalidCastException)
            {
                throw cannotRead(original, target, e.Message);
            }
        }

        throw cannotRead(original, target,
            $"Bobcat has no way to read a {target.Name}. Register one with CellValues.Register<{target.Name}>(text => ...), "
            + $"or give {target.Name} a static Parse (IParsable<{target.Name}>), a TypeConverter, "
            + "or a public constructor taking a string");
    }

    /// <summary>
    /// The conventional reader for a type the built-in readers do not know, or null when it has none:
    /// <see cref="IParsable{TSelf}"/>, then a string-converting <see cref="TypeConverter"/>, then a
    /// public <c>ctor(string)</c>. Resolved once per type.
    /// </summary>
    private static Func<string, object?>? conventionFor(Type target)
    {
        var parsable = target.GetInterfaces().FirstOrDefault(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IParsable<>)
                            && i.GetGenericArguments()[0] == target);
        if (parsable != null && parseMethodOf(target, parsable) is { } parse)
            return text => parse.Invoke(null, [text, CultureInfo.InvariantCulture]);

        var converter = TypeDescriptor.GetConverter(target);
        if (converter.GetType() != typeof(TypeConverter) && converter.CanConvertFrom(typeof(string)))
            return text => converter.ConvertFromString(null, CultureInfo.InvariantCulture, text);

        if (target.GetConstructor([typeof(string)]) is { } constructor)
            return text => constructor.Invoke([text]);

        return null;
    }

    private static MethodInfo? parseMethodOf(Type target, Type parsable)
    {
        // The interface map finds Parse whether the type declares it publicly or implements it
        // explicitly, which a public-method lookup would miss.
        var map = target.GetInterfaceMap(parsable);
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            var parameters = map.InterfaceMethods[i].GetParameters();
            if (map.InterfaceMethods[i].Name == "Parse" && parameters.Length == 2
                                                       && parameters[0].ParameterType == typeof(string))
                return map.TargetMethods[i];
        }

        return null;
    }

    private static object? invoke(Func<string, object?> read, string raw, Type target, string original)
    {
        try
        {
            return read(raw);
        }
        catch (Exception e) when (e is not (SpecCriticalException or SpecCatastrophicException or BadCellException
                                      or OperationCanceledException))
        {
            var inner = e is TargetInvocationException { InnerException: { } thrown } ? thrown : e;
            throw new BadCellException(
                $"The cell '{original}' could not be read as {target.Name}: {inner.Message}", inner);
        }
    }

    private static BadCellException cannotRead(string text, Type target, string because)
        => new($"The cell '{text}' could not be read as {target.Name}: {because}");
}
