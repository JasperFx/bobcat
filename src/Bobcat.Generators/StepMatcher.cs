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

        foreach (var method in fixture.AllStepMethods())
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

    public class TableGrammarMatch
    {
        public TableGrammarInfo Grammar { get; set; } = null!;
        public List<string> ExtractedValues { get; set; } = new();
    }

    /// <summary>
    /// Match a step against the compilation's table grammars. A table grammar declares its own
    /// step text and is deliberately keyword-agnostic — "the following customers exist" reads as
    /// a Given, a When, or a Then depending on the spec, and the text carries the meaning.
    /// </summary>
    public static TableGrammarMatch? MatchTableGrammar(StepInfo step, IEnumerable<TableGrammarInfo> grammars)
    {
        var candidates = new List<TableGrammarMatch>();

        foreach (var grammar in grammars)
        {
            if (grammar.ParsedExpression == null) continue;

            var values = CucumberExpressionParser.TryMatch(grammar.ParsedExpression, step.Text);
            if (values != null)
            {
                candidates.Add(new TableGrammarMatch { Grammar = grammar, ExtractedValues = values });
            }
        }

        if (candidates.Count == 0) return null;
        if (candidates.Count > 1)
        {
            var names = string.Join(", ", candidates.Select(c => c.Grammar.ClassName));
            throw new InvalidOperationException(
                $"Ambiguous table-grammar match for '{step.Text}': matches {names}");
        }

        return candidates[0];
    }
}
