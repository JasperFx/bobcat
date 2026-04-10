using System;
using System.Collections.Generic;
using System.Linq;

namespace Bobcat.Generators;

/// <summary>
/// Matches Gherkin step text to fixture step methods using Cucumber Expressions.
/// </summary>
public static class StepMatcher
{
    public class MatchResult
    {
        public StepMethodInfo Method { get; set; } = null!;
        public List<string> ExtractedValues { get; set; } = new();
    }

    /// <summary>
    /// Find the fixture method that matches a step's keyword and text.
    /// Returns null if no match, throws if ambiguous.
    /// </summary>
    public static MatchResult? Match(StepInfo step, FixtureInfo fixture)
    {
        var targetKind = step.ResolvedKeyword.Trim() switch
        {
            "Given" => "Given",
            "When" => "When",
            "Then" => "Then",
            _ => step.ResolvedKeyword.Trim()
        };

        var candidates = new List<(StepMethodInfo method, List<string> values)>();

        foreach (var method in fixture.StepMethods)
        {
            // Check kind match — "Check" methods match as "Then"
            var methodKind = method.StepKind == "Check" ? "Then" : method.StepKind;
            if (!string.Equals(methodKind, targetKind, StringComparison.OrdinalIgnoreCase))
                continue;

            if (method.ParsedExpression == null)
                continue;

            var values = CucumberExpressionParser.TryMatch(method.ParsedExpression, step.Text);
            if (values != null)
            {
                candidates.Add((method, values));
            }
        }

        if (candidates.Count == 0) return null;
        if (candidates.Count > 1)
        {
            var names = string.Join(", ", candidates.Select(c => c.method.MethodName));
            throw new InvalidOperationException(
                $"Ambiguous step match for '{step.Text}': matches {names}");
        }

        return new MatchResult
        {
            Method = candidates[0].method,
            ExtractedValues = candidates[0].values
        };
    }
}
