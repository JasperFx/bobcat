using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bobcat.Generators;

/// <summary>
/// Issue #110: a specification written as an ordinary xUnit or TUnit test, whose steps are
/// declared by <b>marker comments</b> rather than by a fluent API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a generator, and not a runtime hook.</b> Comments are erased by the compiler — nothing
/// at runtime can see <c>// Given a proposed appointment</c>. Every other authoring style Bobcat
/// supports could in principle be recorded as it executes; this one cannot, so reading the syntax
/// tree is not a design preference here, it is the only place the information exists.
/// </para>
/// <para>
/// <b>What the test body is.</b> Ordinary code that runs under the team's own runner: breakpoints
/// work, there are no lambdas, and the "compose, then execute" split that <c>Specification</c>
/// needs does not apply. The generator contributes the <i>rendering</i> — the ordered steps and
/// the <c>{Feature}/{Scenario}</c> identity — and the runner contributes the verdict.
/// </para>
/// <para>
/// <b>Minimal edits, deliberately.</b> The target is an existing xUnit suite (Marten's
/// DaemonTests is the first real subject), so opting in is one class-level attribute plus
/// comments. No base class, no method signature change, no restructuring — anything more and a
/// large existing suite cannot adopt it incrementally.
/// </para>
/// </remarks>
internal static class MarkerCommentSpecs
{
    internal const string AttributeName = "BobcatFeature";

    /// <summary>The step keywords a marker comment may open with, longest first so "And" inside
    /// a sentence cannot be mistaken for a keyword.</summary>
    private static readonly string[] Keywords = { "Given", "When", "Then", "And", "But" };

    internal sealed class MarkedSpec
    {
        public string FeatureTitle = "";
        public readonly List<MarkedScenario> Scenarios = new();
    }

    internal sealed class MarkedScenario
    {
        public string Title = "";
        public readonly List<MarkedStep> Steps = new();

        /// <summary>A test with no marker comments at all: it runs, but it renders as nothing.</summary>
        public bool IsUnmarked => Steps.Count == 0;
    }

    internal sealed class MarkedStep
    {
        public string Keyword = "";
        public string Text = "";

        /// <summary>1-based line of the comment, kept so a failure can later be mapped back to the
        /// step it fell inside — the open question on #110, and cheap to carry now.</summary>
        public int Line;
    }

    public static MarkedSpec? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var declaration = (ClassDeclarationSyntax)ctx.Node;
        var title = featureAttributeTitle(declaration, ctx.SemanticModel, ct, out var marked);
        if (!marked) return null;

        var spec = new MarkedSpec
        {
            FeatureTitle = CodeFirstNaming.FeatureTitle(declaration.Identifier.Text, title)
        };

        foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (!IsTestMethod(method)) continue;

            var scenario = new MarkedScenario
            {
                Title = CodeFirstNaming.ScenarioTitle(method.Identifier.Text, null)
            };

            foreach (var step in StepsIn(method)) scenario.Steps.Add(step);

            spec.Scenarios.Add(scenario);
        }

        return spec.Scenarios.Count == 0 ? null : spec;
    }

    /// <summary>
    /// Any method a test runner would call. Matched on attribute NAME rather than on a symbol,
    /// so the generator needs no reference to xUnit, TUnit or NUnit — Bobcat cannot depend on a
    /// runner it is trying to be neutral about.
    /// </summary>
    internal static bool IsTestMethod(MethodDeclarationSyntax method)
        => method.AttributeLists
            .SelectMany(list => list.Attributes)
            .Select(a => shortName(a.Name.ToString()))
            .Any(n => n is "Fact" or "Theory" or "Test" or "TestCase");

    /// <summary>
    /// The marker comments in a method body, in source order.
    /// </summary>
    /// <remarks>
    /// Read from the leading trivia of each statement rather than by scanning the file for
    /// comment tokens: it keeps the steps ordered by the statement they introduce, ignores a
    /// comment trailing on the same line as code, and means a comment inside a nested block still
    /// belongs to the statement it precedes.
    /// </remarks>
    internal static IEnumerable<MarkedStep> StepsIn(MethodDeclarationSyntax method)
    {
        if (method.Body is null) yield break;

        foreach (var trivia in method.Body.DescendantTrivia())
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)) continue;

            var step = Parse(trivia.ToString());
            if (step is null) continue;

            step.Line = trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            yield return step;
        }
    }

    /// <summary>
    /// <c>// Given a proposed appointment</c> → Given / "a proposed appointment". Anything that
    /// does not open with a keyword is an ordinary comment and stays invisible — a test is full of
    /// those, and treating them as steps would make the rendering worse than nothing.
    /// </summary>
    internal static MarkedStep? Parse(string comment)
    {
        // Whitespace BEFORE the slashes, then the slashes, then whitespace after: a comment read
        // from trivia carries its indentation, so trimming slashes first strips nothing at all.
        var text = comment.Trim().TrimStart('/').Trim();
        if (text.Length == 0) return null;

        foreach (var keyword in Keywords)
        {
            if (!text.StartsWith(keyword, StringComparison.Ordinal)) continue;

            // "Givenchy" is not a step. The keyword has to be a word on its own.
            if (text.Length > keyword.Length && !char.IsWhiteSpace(text[keyword.Length])) continue;

            var remainder = text.Substring(keyword.Length).Trim();
            if (remainder.Length == 0) return null;

            return new MarkedStep { Keyword = keyword, Text = remainder };
        }

        return null;
    }


    /// <summary>
    /// The registration a marked assembly carries: every scenario's declared steps, in order,
    /// keyed by the identity the recorder will ask for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A module initializer, so opting in really is only comments.</b> The steps have to reach
    /// the runtime before the first test runs, and nothing in the test body can be made to carry
    /// them. Anything else — a call in a fixture, an attribute per method, a registration the
    /// author remembers — would defeat the point of an authoring style whose entire promise is
    /// that an existing suite adopts it by writing sentences.
    /// </para>
    /// <para>
    /// Emitted only when at least one class is marked, so an assembly that never heard of #110
    /// gains no initializer and no startup cost.
    /// </para>
    /// </remarks>
    public static string Emit(IEnumerable<MarkedSpec> specs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine($"namespace {StepInterceptors.Namespace}");
        sb.AppendLine("{");
        sb.AppendLine("    internal static class BobcatDeclaredStepRegistration");
        sb.AppendLine("    {");
        sb.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("        internal static void Register()");
        sb.AppendLine("        {");

        foreach (var spec in specs)
        {
            foreach (var scenario in spec.Scenarios)
            {
                // A test with no marker comments is not a specification. Registering it empty
                // would announce a scenario with nothing to say, which reads as a defect.
                if (scenario.IsUnmarked) continue;

                var steps = string.Join(", ", scenario.Steps.Select(step =>
                    $"new global::Bobcat.DeclaredStep({Quote(step.Keyword)}, {Quote(step.Text)}, {step.Line})"));

                sb.AppendLine(
                    $"            global::Bobcat.DeclaredSteps.Register({Quote(spec.FeatureTitle + "/" + scenario.Title)}, {steps});");
            }
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>True when anything at all was declared — the gate on emitting the initializer.</summary>
    public static bool HasSteps(IEnumerable<MarkedSpec> specs)
        => specs.Any(spec => spec.Scenarios.Any(scenario => !scenario.IsUnmarked));

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string? featureAttributeTitle(
        ClassDeclarationSyntax declaration, SemanticModel model, CancellationToken ct, out bool marked)
    {
        marked = false;

        foreach (var attribute in declaration.AttributeLists.SelectMany(list => list.Attributes))
        {
            if (shortName(attribute.Name.ToString()) != AttributeName) continue;

            marked = true;

            var argument = attribute.ArgumentList?.Arguments.FirstOrDefault();
            if (argument is null) return null;

            return model.GetConstantValue(argument.Expression, ct).Value as string;
        }

        return null;
    }

    private static string shortName(string name)
    {
        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0) name = name.Substring(lastDot + 1);
        return name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name.Substring(0, name.Length - "Attribute".Length)
            : name;
    }
}
