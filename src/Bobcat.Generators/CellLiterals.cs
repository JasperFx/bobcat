using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bobcat.Generators;

/// <summary>
/// Turns one written value — a Cucumber capture, a data-table cell, a doc string — into the C#
/// expression the generated step passes as an argument, and says when it cannot.
/// </summary>
/// <remarks>
/// <para>
/// One function, two callers, on purpose. <see cref="CodeEmitter"/> asks it for the literal, and
/// the analysis pass asks it whether the value is readable at all so an unreadable one is
/// <b>BOBCAT030</b> rather than a compiler error inside generated code. Before this the two
/// answers came from different code: <c>ToCSharpLiteral</c> emitted whatever text it was given
/// for anything outside a short list of types, so <c>Given a cell oops</c> bound to an <c>int</c>
/// became the identifier <c>oops</c> (CS0103) and an <c>enum</c>, <c>DateTime</c> or
/// <c>int?</c> parameter got a string literal (CS1503) — a broken build in a file the author
/// cannot open, over a step that matched.
/// </para>
/// <para>
/// The set of types handled here is exactly the set
/// <see cref="BobcatGenerator.IsSimpleType(Microsoft.CodeAnalysis.ITypeSymbol)"/> calls a value
/// rather than a service. That is the invariant worth keeping: a parameter the binder decided to
/// read from the document must be a parameter this can write.
/// </para>
/// </remarks>
public static class CellLiterals
{
    /// <summary>
    /// The values this emit could not read, collected as the emitter binds them.
    /// </summary>
    /// <remarks>
    /// Thread-static and opened for the length of one <see cref="CodeEmitter.EmitFeature"/>, which
    /// is synchronous on one thread. Collecting at the binding site rather than in a second
    /// validation pass is the whole point: a validator that re-derived which parameter takes which
    /// cell would be a second opinion about binding, free to drift from the emitter's — and the
    /// symptom of drift is a feature that emits code that does not compile, which is the defect
    /// BOBCAT030 exists to remove.
    /// </remarks>
    [ThreadStatic] private static List<UnreadableValue>? _collected;

    [ThreadStatic] private static string? _step;

    /// <summary>One written value the generator could not read as the parameter it binds to.</summary>
    public sealed class UnreadableValue
    {
        public string Step = "";
        public string Parameter = "";
        public string Value = "";
        public string Problem = "";
    }

    /// <summary>Starts collecting; the caller must <see cref="StopCollecting"/> in a finally.</summary>
    public static List<UnreadableValue> StartCollecting()
    {
        _collected = new List<UnreadableValue>();
        _step = "";
        return _collected;
    }

    public static void StopCollecting()
    {
        _collected = null;
        _step = null;
    }

    /// <summary>The step being emitted, so a report can name it.</summary>
    public static void CurrentStep(string text) => _step = text;

    /// <summary>
    /// The C# expression for <paramref name="value"/> as <paramref name="parameter"/>'s type, or
    /// <c>null</c> with <paramref name="problem"/> set to a sentence naming what could not be read.
    /// </summary>
    public static string? TryConvert(string value, ParameterInfo parameter, out string? problem)
        => TryConvert(value, parameter.Type, parameter.QualifiedType, parameter.EnumMembers, out problem);

    public static string? TryConvert(string value, string csharpType, string qualifiedType,
        IReadOnlyList<string> enumMembers, out string? problem)
    {
        problem = null;

        // A type name in the step text ({event}, {aggregate}, …) has already been resolved to a
        // global::-qualified name by the generator; it is emitted as a type, not parsed.
        if (csharpType == CucumberExpressionParser.TypeCSharpType) return $"typeof({value})";

        var nullable = csharpType.EndsWith("?", StringComparison.Ordinal);
        var bare = nullable ? csharpType.Substring(0, csharpType.Length - 1) : csharpType;
        var bareQualified = qualifiedType.EndsWith("?", StringComparison.Ordinal)
            ? qualifiedType.Substring(0, qualifiedType.Length - 1)
            : qualifiedType;

        // An empty cell against a nullable value type is the one way a specification can say
        // "no value". For a reference type the empty string IS the value.
        if (nullable && value.Length == 0 && IsValueType(bare, enumMembers)) return "null";

        if (enumMembers.Count > 0) return enumLiteral(value, bare, bareQualified, enumMembers, out problem);

        switch (bare)
        {
            case "string":
                return Quote(value);

            // Not parsed: the honest reading of an untyped cell is the text that was written.
            case "object":
                return Quote(value);

            case "bool":
                if (bool.TryParse(value, out var b)) return b ? "true" : "false";
                problem = $"'{value}' is not a bool — write true or false";
                return null;

            case "char":
                if (value.Length == 1) return charLiteral(value[0]);
                problem = $"'{value}' is not a char — a single character was expected";
                return null;

            case "int":
                return integer(value, bare, "", out problem);
            case "long":
                return integer(value, bare, "L", out problem);
            case "uint":
                return integer(value, bare, "u", out problem);
            case "ulong":
                return integer(value, bare, "ul", out problem);

            // Cast forms: C# has no literal suffix for the small integers.
            case "short":
            case "ushort":
            case "byte":
            case "sbyte":
                return smallInteger(value, bare, out problem);

            case "float":
                return real(value, bare, "f", out problem);
            case "double":
                return real(value, bare, "d", out problem);
            case "decimal":
                return real(value, bare, "m", out problem);

            case "System.Guid":
                if (Guid.TryParse(value, out _))
                    return $"global::System.Guid.Parse({Quote(value)})";
                problem = $"'{value}' is not a Guid";
                return null;

            case "System.DateTime":
                return parsed(value, bare, "global::System.DateTime",
                    v => DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                    out problem);

            case "System.DateTimeOffset":
                return parsed(value, bare, "global::System.DateTimeOffset",
                    v => DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                    out problem);

            case "System.DateOnly":
                return parsed(value, bare, "global::System.DateOnly",
                    v => DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                    out problem);

            case "System.TimeOnly":
                return parsed(value, bare, "global::System.TimeOnly",
                    v => TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out _),
                    out problem);

            case "System.TimeSpan":
                return parsed(value, bare, "global::System.TimeSpan",
                    v => TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out _),
                    out problem);

            case "System.Uri":
                if (Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out _))
                    return $"new global::System.Uri({Quote(value)}, global::System.UriKind.RelativeOrAbsolute)";
                problem = $"'{value}' is not a URI";
                return null;
        }

        // A type the binder should never have called a value. Left as a string literal so the
        // shape of the failure does not change for anything already compiling.
        return Quote(value);
    }

    /// <summary>
    /// The literal, or an expression that fails legibly at run time when the value cannot be read.
    /// </summary>
    /// <remarks>
    /// The analysis pass reports BOBCAT030 and suppresses the feature, so the failing form should
    /// be unreachable. It exists as the floor: a binding path the validator does not walk still
    /// produces a sentence naming the cell rather than a compiler error in generated code.
    /// </remarks>
    public static string Convert(string value, ParameterInfo parameter)
    {
        var literal = TryConvert(value, parameter, out var problem);
        if (literal != null) return literal;

        _collected?.Add(new UnreadableValue
        {
            Step = _step ?? "",
            Parameter = parameter.Name,
            Value = value,
            Problem = problem ?? ""
        });

        return $"global::Bobcat.Runtime.CellValues.Unreadable<{parameter.QualifiedType}>(" +
               $"{Quote(parameter.Name)}, {Quote(value)}, {Quote(problem ?? "")})";
    }

    public static string Convert(string value, string csharpType)
    {
        var literal = TryConvert(value, csharpType, csharpType, Array.Empty<string>(), out _);
        return literal ?? Quote(value);
    }

    /// <summary>A C# string literal, escaped.</summary>
    public static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    private static string? enumLiteral(string value, string bare, string qualified,
        IReadOnlyList<string> members, out string? problem)
    {
        problem = null;

        var member = members.FirstOrDefault(m => string.Equals(m, value, StringComparison.OrdinalIgnoreCase));
        if (member != null) return $"{qualified}.{member}";

        // A numeric cell is a legal enum value even where no member names it — [Flags] combinations
        // and forward-compatible codes both arrive that way.
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return $"({qualified})({value})";

        problem = $"'{value}' is not one of {bare}'s values ({string.Join(", ", members)})";
        return null;
    }

    private static string? integer(string value, string type, string suffix, out string? problem)
    {
        problem = null;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return value + suffix;
        }

        problem = $"'{value}' is not {article(type)} {type}";
        return null;
    }

    private static string? smallInteger(string value, string type, out string? problem)
    {
        problem = null;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return $"({type}){value}";

        problem = $"'{value}' is not {article(type)} {type}";
        return null;
    }

    private static string? real(string value, string type, string suffix, out string? problem)
    {
        problem = null;
        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return value + suffix;
        }

        problem = $"'{value}' is not {article(type)} {type}";
        return null;
    }

    /// <summary>
    /// A type with no literal form: emitted as an invariant-culture parse, validated here with the
    /// same parse so a bad value is a build error rather than a run-time one.
    /// </summary>
    private static string? parsed(string value, string type, string qualified,
        Func<string, bool> canParse, out string? problem)
    {
        problem = null;
        if (canParse(value))
            return $"{qualified}.Parse({Quote(value)}, global::System.Globalization.CultureInfo.InvariantCulture)";

        problem = $"'{value}' is not {article(shortName(type))} {shortName(type)}";
        return null;
    }

    private static bool IsValueType(string bare, IReadOnlyList<string> enumMembers)
    {
        if (enumMembers.Count > 0) return true;

        switch (bare)
        {
            case "string":
            case "object":
            case "System.Uri":
                return false;
            default:
                return true;
        }
    }

    private static string charLiteral(char c) => c switch
    {
        '\'' => "'\\''",
        '\\' => "'\\\\'",
        '\n' => "'\\n'",
        '\r' => "'\\r'",
        _ => $"'{c}'"
    };

    private static string shortName(string type)
    {
        var dot = type.LastIndexOf('.');
        return dot >= 0 ? type.Substring(dot + 1) : type;
    }

    private static string article(string type)
        => "aeiou".IndexOf(char.ToLowerInvariant(type[0])) >= 0 ? "an" : "a";
}
