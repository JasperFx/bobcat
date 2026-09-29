using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bobcat.Generators;

/// <summary>
/// Parses Cucumber Expressions (e.g., "the left operand is {int}") into
/// regex patterns for matching and parameter type information for code generation.
/// Also handles raw regex patterns (detected by presence of ^ or regex metacharacters).
/// Runs at compile time in the source generator.
/// </summary>
public static class CucumberExpressionParser
{
    /// <summary>
    /// Built-in parameter types and their regex patterns.
    /// </summary>
    private static readonly Dictionary<string, (string Regex, string CSharpType)> builtInTypes = new()
    {
        ["int"] = (@"(-?\d+)", "int"),
        ["long"] = (@"(-?\d+)", "long"),
        ["float"] = (@"(-?[\d.]+)", "float"),
        ["double"] = (@"(-?[\d.]+)", "double"),
        ["decimal"] = (@"(-?[\d.]+)", "decimal"),
        ["string"] = (@"""([^""]*)""", "string"),
        ["word"] = (@"(\S+)", "string"),
        [""] = (@"(.*)", "string"), // anonymous parameter

        // A type name, resolved against the consuming compilation at generation time and bound to
        // a System.Type parameter as typeof(global::...). {type} is the general form; the rest are
        // the Event Modeling vocabulary the shipped Critter Stack grammars read as — same regex,
        // same binding, different words in the step text. See BobcatGenerator.resolveTypeCaptures.
        ["type"] = (TypeNameRegex, TypeCSharpType),
        ["aggregate"] = (TypeNameRegex, TypeCSharpType),
        ["command"] = (TypeNameRegex, TypeCSharpType),
        ["event"] = (TypeNameRegex, TypeCSharpType),
        ["readmodel"] = (TypeNameRegex, TypeCSharpType),
        ["message"] = (TypeNameRegex, TypeCSharpType),
        // {document} is the document-store vocabulary (issue #270), not an Event Modeling role:
        // a document-backed application has no stream, and stamping one would put an element on a
        // canvas that describes nothing. EventModelEmitter switches on the role words above and
        // lets this one fall through, so it is inert there by construction — same as {type}. It
        // exists so `Given Shipments` reads as a document rather than as the generic
        // `Given documents of type Shipment` that {type} forces.
        ["document"] = (TypeNameRegex, TypeCSharpType),
        // {saga} is Wolverine's saga vocabulary (issue #281), and inert in EventModelEmitter for the
        // same reason as {document}: a saga's state is not an aggregate, an event or a read model,
        // and a wrong element on the canvas is worse than a missing one. It earns its own word so
        // a misspelled saga is BOBCAT011 at build and the step reads as a saga, not a {type}.
        ["saga"] = (TypeNameRegex, TypeCSharpType),
    };

    /// <summary>A simple or dotted type name: <c>Account</c>, <c>Banking.Account</c>, <c>Outer+Nested</c>.</summary>
    public const string TypeNameRegex = @"([A-Za-z_][\w.+]*)";

    /// <summary>The C# type a type-name capture binds to; its literal is a <c>typeof(...)</c>.</summary>
    public const string TypeCSharpType = "System.Type";

    /// <summary>The parameter-type names that capture a type name (<c>{type}</c>, <c>{aggregate}</c>, …).</summary>
    public static IEnumerable<string> TypeParameterNames
        => builtInTypes.Where(kv => kv.Value.CSharpType == TypeCSharpType).Select(kv => kv.Key);

    public class ParsedExpression
    {
        public string RegexPattern { get; }
        public List<ParameterCapture> Parameters { get; }
        public bool IsRawRegex { get; }

        public ParsedExpression(string regexPattern, List<ParameterCapture> parameters, bool isRawRegex)
        {
            RegexPattern = regexPattern;
            Parameters = parameters;
            IsRawRegex = isRawRegex;
        }
    }

    public class ParameterCapture
    {
        public string CSharpType { get; }
        public int GroupIndex { get; }

        /// <summary>
        /// The parameter-type word as written in the expression — <c>int</c>, <c>string</c>,
        /// <c>command</c>, <c>event</c>. Null for a raw-regex group, which has no word.
        /// </summary>
        /// <remarks>
        /// Kept because the six type-name words all share one <see cref="CSharpType"/>
        /// (<c>System.Type</c>), so without the word a resolved capture says "this is a type" but
        /// not <em>which role</em> the type plays. Issue #106's descriptor is entirely about the
        /// roles: a <c>{command}</c> and an <c>{event}</c> land in different slots and different
        /// swim lanes, and every step binding stayed correct while that distinction was thrown
        /// away here.
        /// </remarks>
        public string? ParameterName { get; }

        /// <summary>
        /// The METHOD parameter this capture binds to, when the placeholder named one — the
        /// Storyteller <c>[FormatAs]</c> reading of <c>{sum}</c>. Null for a Cucumber capture, which
        /// binds positionally.
        /// </summary>
        /// <remarks>
        /// Distinct from <c>ParameterName</c>, which holds the placeholder's TYPE word (<c>int</c>,
        /// <c>aggregate</c>) and is what the Event Model emitter reads to tell a <c>{command}</c>
        /// from an <c>{event}</c>. Two different questions about the same placeholder, and folding
        /// them together would make a parameter called <c>event</c> stamp an Event Modeling role.
        /// </remarks>
        public string? BoundParameterName { get; set; }

        /// <summary>
        /// True when the placeholder named no parameter and stands for the method's RETURN value —
        /// Storyteller's <c>[return: AliasAs("sum")]</c> cell, written into the sentence.
        /// </summary>
        /// <remarks>
        /// The Cucumber form of the same thing is positional: a capture beyond the method's
        /// parameters is the expected value. A named template has to say it outright, because a
        /// name cannot be counted.
        /// </remarks>
        public bool IsExpected { get; set; }

        /// <summary>
        /// Which part of the return value this capture is the expected value of — a tuple element's
        /// name. Null for the single-return case, where there is only one thing it could be.
        /// </summary>
        public string? ExpectedName { get; set; }

        public ParameterCapture(string csharpType, int groupIndex, string? parameterName = null)
        {
            CSharpType = csharpType;
            GroupIndex = groupIndex;
            ParameterName = parameterName;
        }
    }

    /// <summary>
    /// Parse a step expression (Cucumber Expression or raw regex) into a regex pattern
    /// and parameter type list.
    /// </summary>
    public static ParsedExpression Parse(string expression) => Parse(expression, []);

    /// <summary>
    /// Parse a step expression into a regex and its captures, resolving any placeholder that names
    /// one of <paramref name="parameters"/> against that parameter's own type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two syntaxes, one parser.</b> A Cucumber expression captures by TYPE — <c>{int}</c> — and
    /// binds positionally. A Storyteller <c>[FormatAs]</c> template captures by PARAMETER NAME —
    /// <c>{sum}</c> — and the parameter's declared type says how to read the cell, which means the
    /// step text says what the value <i>is</i> rather than merely what type it has.
    /// </para>
    /// <para>
    /// <b>Decided per placeholder, and the built-in word wins.</b> <c>{int}</c> stays a Cucumber
    /// capture even on a method with a parameter called <c>int</c>, so no expression that compiled
    /// before means anything different now. A placeholder that is not a built-in type word and does
    /// name a parameter is the named form; one that is neither is still an error, as it was.
    /// </para>
    /// <para>
    /// <b>Mixing them is allowed, deliberately.</b> <c>"the {aggregate} has {count} events"</c> is a
    /// natural thing to write, and there is no ambiguity in it: each placeholder is resolved on its
    /// own and a named one binds by name whatever the regex came from.
    /// </para>
    /// </remarks>
    public static ParsedExpression Parse(string expression, IReadOnlyList<StepParameter> parameters)
        => Parse(expression, parameters, null);

    /// <param name="returnType">
    /// The method's return type, so a placeholder naming no parameter can be read as the EXPECTED
    /// return value — <c>"The value should be {value}"</c> over a <c>double</c>-returning method,
    /// which is the single most common Storyteller grammar there is. Null for a method with nothing
    /// to compare, where such a placeholder stays an error.
    /// </param>
    public static ParsedExpression Parse(
        string expression, IReadOnlyList<StepParameter> parameters, string? returnType)
        => Parse(expression, parameters, returnType, []);

    /// <param name="results">
    /// The comparable parts of the return value — a named tuple's elements. A placeholder naming one
    /// is that element's expected cell, typed from the element rather than from the tuple, which is
    /// what lets one sentence make several assertions on an <c>async</c> method.
    /// </param>
    public static ParsedExpression Parse(
        string expression, IReadOnlyList<StepParameter> parameters, string? returnType,
        IReadOnlyList<StepParameter> results)
    {
        if (isRawRegex(expression))
        {
            return parseRawRegex(expression);
        }

        return parseCucumberExpression(expression, parameters, returnType, results);
    }

    private static bool isRawRegex(string expression)
    {
        return expression.StartsWith("^") || expression.Contains("\\d") || expression.Contains("(?");
    }

    private static ParsedExpression parseCucumberExpression(
        string expression, IReadOnlyList<StepParameter> parameters, string? returnType,
        IReadOnlyList<StepParameter> results)
    {
        var captures = new List<ParameterCapture>();
        var regex = new StringBuilder();
        regex.Append('^');

        var groupIndex = 1;
        var i = 0;

        while (i < expression.Length)
        {
            if (expression[i] == '{')
            {
                var end = expression.IndexOf('}', i);
                if (end < 0)
                    throw new ArgumentException($"Unclosed '{{' in expression: {expression}");

                var typeName = expression.Substring(i + 1, end - i - 1).Trim();

                if (builtInTypes.TryGetValue(typeName, out var typeInfo))
                {
                    regex.Append(typeInfo.Regex);

                    var capture = new ParameterCapture(typeInfo.CSharpType, groupIndex, typeName);

                    // A built-in word that ALSO names a parameter binds by name — strictly more
                    // robust than by position, and it costs nothing.
                    if (named(typeName, parameters) is not null) capture.BoundParameterName = typeName;

                    captures.Add(capture);
                }
                else if (named(typeName, parameters) is { } parameter)
                {
                    regex.Append(regexForType(parameter.CSharpType));
                    captures.Add(new ParameterCapture(parameter.CSharpType, groupIndex, typeName)
                    {
                        BoundParameterName = typeName
                    });
                }
                else if (named(typeName, results) is { } element)
                {
                    // One element of a tuple return, named in the sentence: "the Sum should be {sum}
                    // and the Product should be {product}".
                    regex.Append(regexForType(element.CSharpType));
                    captures.Add(new ParameterCapture(element.CSharpType, groupIndex, typeName)
                    {
                        IsExpected = true,
                        ExpectedName = element.Name
                    });
                }
                else if (returnType is { Length: > 0 } && returnType != "void")
                {
                    // The return-value cell, named. Storyteller wrote it as [return: AliasAs("sum")]
                    // plus a {sum} in the format; here the name in the sentence IS the alias.
                    regex.Append(regexForType(returnType));
                    captures.Add(new ParameterCapture(returnType, groupIndex, typeName) { IsExpected = true });
                }
                else
                {
                    throw new ArgumentException(
                        $"'{{{typeName}}}' in expression '{expression}' is neither a built-in parameter type, "
                        + "nor the name of a parameter on the method, nor usable as the expected return "
                        + "value (the method returns nothing)");
                }

                groupIndex++;
                i = end + 1;
            }
            else if (expression[i] == '(')
            {
                // Optional text: (word) or (word1/word2)
                var end = expression.IndexOf(')', i);
                if (end < 0)
                    throw new ArgumentException($"Unclosed '(' in expression: {expression}");

                var content = expression.Substring(i + 1, end - i - 1);
                if (content.Contains("/"))
                {
                    // Alternation: (word1/word2)
                    var alts = content.Split('/');
                    regex.Append("(?:");
                    regex.Append(string.Join("|", alts.Select(escapeForRegex)));
                    regex.Append(')');
                }
                else
                {
                    // Optional text
                    regex.Append("(?:");
                    regex.Append(escapeForRegex(content));
                    regex.Append(")?");
                }
                i = end + 1;
            }
            else
            {
                regex.Append(escapeChar(expression[i]));
                i++;
            }
        }

        regex.Append('$');

        return new ParsedExpression(regex.ToString(), captures, false);
    }

    private static StepParameter? named(string name, IReadOnlyList<StepParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.Name == name) return parameter;
        }

        return null;
    }

    /// <summary>
    /// The regex a named placeholder gets, from the parameter's own declared type.
    /// </summary>
    /// <remarks>
    /// A string gets a NON-GREEDY run rather than <c>(\S+)</c>: a Storyteller sentence routinely puts
    /// a phrase in a cell ("Start with the number twenty one"), and with the expression anchored at
    /// both ends the literal text between placeholders is what bounds it. Numbers get the tighter
    /// digit patterns, so a sentence ending in a number cannot swallow the words before it.
    /// </remarks>
    internal static string regexForType(string csharpType)
        => csharpType switch
        {
            "int" or "long" or "short" or "byte" or "sbyte" or "uint" or "ulong" or "ushort" => @"(-?\d+)",
            "float" or "double" or "decimal" => @"(-?[\d.]+)",
            "bool" => "(true|false|True|False)",
            TypeCSharpType => TypeNameRegex,
            "string" => "(.+?)",
            _ => @"(\S+)"
        };

    private static ParsedExpression parseRawRegex(string expression)
    {
        // Raw regex — extract group count for parameter mapping
        var parameters = new List<ParameterCapture>();
        var groupIndex = 1;

        // Count capturing groups (non-escaped parentheses that aren't non-capturing)
        for (var i = 0; i < expression.Length; i++)
        {
            if (expression[i] == '\\')
            {
                i++; // skip escaped char
                continue;
            }

            if (expression[i] == '(' && i + 1 < expression.Length && expression[i + 1] != '?')
            {
                // Capturing group — default to string type
                parameters.Add(new ParameterCapture("string", groupIndex));
                groupIndex++;
            }
        }

        return new ParsedExpression(expression, parameters, true);
    }

    private static string escapeChar(char c)
    {
        // Regex metacharacters that need escaping
        return c switch
        {
            '.' or '*' or '+' or '?' or '[' or ']' or '\\' or '$' or '^' or '|' => $"\\{c}",
            _ => c.ToString()
        };
    }

    private static string escapeForRegex(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            sb.Append(escapeChar(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Try to match step text against a parsed expression. Returns parameter values if matched.
    /// Used during source generation to extract literal values from feature file steps.
    /// </summary>
    public static List<string>? TryMatch(ParsedExpression parsed, string stepText)
    {
        var match = Regex.Match(stepText, parsed.RegexPattern);
        if (!match.Success) return null;

        var values = new List<string>();
        foreach (var param in parsed.Parameters)
        {
            if (param.GroupIndex < match.Groups.Count)
            {
                values.Add(match.Groups[param.GroupIndex].Value);
            }
        }

        return values;
    }

    /// <summary>
    /// The matched values in the order <b>the method's parameters</b> want them, when every
    /// value-bearing parameter was named by a placeholder. Otherwise the values unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Cucumber capture binds positionally and a named one binds by name, and the emitter consumes
    /// values positionally — so the reordering happens here, once, rather than every caller learning
    /// about two binding models.
    /// </para>
    /// <para>
    /// <b>Only when the cover is complete.</b> A partly-named expression falls back to positional,
    /// which is correct whenever the placeholders are in parameter order — and they are, in every
    /// natural sentence. Reordering a partial cover would have to invent a slot for the parameters no
    /// placeholder named, and inventing a slot shifts every value after it.
    /// </para>
    /// </remarks>
    public static List<string> OrderForParameters(
        ParsedExpression parsed, List<string> values, IReadOnlyList<StepParameter> valueParameters)
        => OrderForParameters(parsed, values, valueParameters, []);

    /// <param name="results">
    /// The return's comparable parts, in declaration order. Their expected values land after the
    /// parameters' — which is where the emitter reads them, mirroring how <c>out</c> parameters
    /// consume trailing captures today.
    /// </param>
    public static List<string> OrderForParameters(
        ParsedExpression parsed, List<string> values, IReadOnlyList<StepParameter> valueParameters,
        IReadOnlyList<StepParameter> results)
    {
        if (values.Count != parsed.Parameters.Count) return values;

        var byName = new Dictionary<string, string>();
        for (var i = 0; i < parsed.Parameters.Count; i++)
        {
            var name = parsed.Parameters[i].BoundParameterName;
            if (name != null) byName[name] = values[i];
        }

        if (byName.Count == 0) return values;

        var ordered = new List<string>(values.Count);
        foreach (var parameter in valueParameters)
        {
            if (!byName.TryGetValue(parameter.Name, out var value)) return values;
            ordered.Add(value);
        }

        // Expected values last. For a tuple return they are ordered by the ELEMENTS' declaration
        // order rather than by where they appear in the sentence, so the emitter can keep reading them
        // positionally — the same model `out` parameters already use.
        if (results.Count > 0)
        {
            foreach (var element in results)
            {
                var found = false;
                for (var i = 0; i < parsed.Parameters.Count && !found; i++)
                {
                    if (parsed.Parameters[i].ExpectedName != element.Name) continue;

                    ordered.Add(values[i]);
                    found = true;
                }

                // A sentence that names only some of the elements is not a partial comparison — it is
                // an expression the author has not finished writing, and guessing at the rest would
                // report cells nobody asked for.
                if (!found) return values;
            }

            return ordered;
        }

        for (var i = 0; i < parsed.Parameters.Count; i++)
        {
            if (parsed.Parameters[i].IsExpected) ordered.Add(values[i]);
        }

        return ordered;
    }

    /// <summary>
    /// Generate a C# literal expression for a captured value with the given type.
    /// </summary>
    public static string ToCSharpLiteral(string value, string csharpType)
    {
        return csharpType switch
        {
            "int" => value,
            "long" => $"{value}L",
            "float" => $"{value}f",
            "double" => $"{value}d",
            "decimal" => $"{value}m",
            "string" => $"\"{escapeCSharpString(value)}\"",

            // A named placeholder binds to the parameter's OWN type, so these now reach the emitter
            // where before only the Cucumber words could. Without them a bool or a Guid parameter got
            // a string literal, which fails in the CONSUMER's build, in a file they cannot edit.
            "bool" => value.ToLowerInvariant() is "true" ? "true" : "false",
            "short" or "byte" or "sbyte" or "ushort" => $"({csharpType}){value}",
            "uint" => $"{value}u",
            "ulong" => $"{value}ul",
            "System.Guid" or "Guid" => $"global::System.Guid.Parse(\"{escapeCSharpString(value)}\")",

            // The value has already been resolved to a global::-qualified name by the generator.
            TypeCSharpType => $"typeof({value})",
            _ => $"\"{escapeCSharpString(value)}\""
        };
    }

    private static string escapeCSharpString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}

/// <summary>
/// One parameter of a step method, as the expression parser needs to see it: a name a placeholder
/// may match, and the type that says how its cell is read.
/// </summary>
/// <remarks>
/// A plain struct, not a record: the generator targets netstandard2.0, which has no
/// <c>IsExternalInit</c>, so a positional record does not compile here.
/// </remarks>
public readonly struct StepParameter
{
    public StepParameter(string name, string csharpType)
    {
        Name = name;
        CSharpType = csharpType;
    }

    public string Name { get; }
    public string CSharpType { get; }
}
