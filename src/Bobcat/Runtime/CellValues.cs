using System.Globalization;
using Bobcat.Engine;
using Bobcat.Engine.Verification;

namespace Bobcat.Runtime;

/// <summary>
/// Reads one written cell — a Gherkin table cell, a capture, a cell of a table literal — as a value
/// of a target type. The one runtime authority on what a cell means.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is one place.</b> There were three: the generator's compile-time literal emission, a
/// private <c>GherkinValue.Convert</c> behind the reflective grammars, and the
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
/// </remarks>
public static class CellValues
{
    /// <summary>The cell as a <typeparamref name="T"/>. The form generated code calls.</summary>
    public static T Read<T>(string text) => (T)Read(text, typeof(T))!;

    /// <summary>
    /// The cell as <paramref name="target"/>, or null where the cell says so and the target allows it.
    /// </summary>
    /// <exception cref="SpecCriticalException">
    /// The cell cannot be read as that type. A critical failure rather than a
    /// <see cref="FormatException"/> from somewhere inside a parse, because the author needs to be
    /// told which cell and what was expected of it.
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
        try
        {
            if (target == typeof(string)) return raw;
            if (target.IsEnum) return Enum.Parse(target, raw, ignoreCase: true);
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

            return System.Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException
                                     or InvalidCastException)
        {
            throw cannotRead(original, target, e.Message);
        }
    }

    private static SpecCriticalException cannotRead(string text, Type target, string because)
        => new($"The cell '{text}' could not be read as {target.Name}: {because}");
}
