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
    private static readonly Dictionary<string, (string Regex, string CSharpType)> BuiltInTypes = new()
    {
        ["int"] = (@"(-?\d+)", "int"),
        ["long"] = (@"(-?\d+)", "long"),
        ["float"] = (@"(-?[\d.]+)", "float"),
        ["double"] = (@"(-?[\d.]+)", "double"),
        ["decimal"] = (@"(-?[\d.]+)", "decimal"),
        ["string"] = (@"""([^""]*)""", "string"),
        ["word"] = (@"(\S+)", "string"),
        [""] = (@"(.*)", "string"), // anonymous parameter
    };

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

        public ParameterCapture(string csharpType, int groupIndex)
        {
            CSharpType = csharpType;
            GroupIndex = groupIndex;
        }
    }

    /// <summary>
    /// Parse a step expression (Cucumber Expression or raw regex) into a regex pattern
    /// and parameter type list.
    /// </summary>
    public static ParsedExpression Parse(string expression)
    {
        if (IsRawRegex(expression))
        {
            return ParseRawRegex(expression);
        }

        return ParseCucumberExpression(expression);
    }

    private static bool IsRawRegex(string expression)
    {
        return expression.StartsWith("^") || expression.Contains("\\d") || expression.Contains("(?");
    }

    private static ParsedExpression ParseCucumberExpression(string expression)
    {
        var parameters = new List<ParameterCapture>();
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

                if (!BuiltInTypes.TryGetValue(typeName, out var typeInfo))
                    throw new ArgumentException($"Unknown parameter type '{{{typeName}}}' in expression: {expression}");

                regex.Append(typeInfo.Regex);
                parameters.Add(new ParameterCapture(typeInfo.CSharpType, groupIndex));
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
                    regex.Append(string.Join("|", alts.Select(EscapeForRegex)));
                    regex.Append(')');
                }
                else
                {
                    // Optional text
                    regex.Append("(?:");
                    regex.Append(EscapeForRegex(content));
                    regex.Append(")?");
                }
                i = end + 1;
            }
            else
            {
                regex.Append(EscapeChar(expression[i]));
                i++;
            }
        }

        regex.Append('$');

        return new ParsedExpression(regex.ToString(), parameters, false);
    }

    private static ParsedExpression ParseRawRegex(string expression)
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

    private static string EscapeChar(char c)
    {
        // Regex metacharacters that need escaping
        return c switch
        {
            '.' or '*' or '+' or '?' or '[' or ']' or '\\' or '$' or '^' or '|' => $"\\{c}",
            _ => c.ToString()
        };
    }

    private static string EscapeForRegex(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            sb.Append(EscapeChar(c));
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
            "string" => $"\"{EscapeCSharpString(value)}\"",
            _ => $"\"{EscapeCSharpString(value)}\""
        };
    }

    private static string EscapeCSharpString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}
