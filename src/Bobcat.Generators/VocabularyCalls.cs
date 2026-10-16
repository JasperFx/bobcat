using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Bobcat.Generators;

/// <summary>
/// bobcat#625: the scenario vocabulary calls a projected test makes — <c>GivenEvents&lt;Shipment&gt;(…)</c>,
/// <c>WhenReceived(new ShipOrder(…))</c>, <c>ThenDocument&lt;Shipment&gt;(…)</c> — read so a preview can show them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Preview only, never the plan.</b> These steps build their sentence from argument values at run time,
/// so there is no template to plan and no interceptor to number them. Putting them in <c>PlannedSteps</c>
/// would make every one of them a step the recorder never reports reaching, greyed out as unreached on
/// every run. They go to <c>VocabularySteps</c>, which only the preview reads.
/// </para>
/// <para>
/// <b>What counts as vocabulary.</b> A method declared on <c>CritterStackScenario</c> or
/// <c>CritterStackSpec</c> or anything deriving from them (so WolverineFx.Bobcat's scenarios and specs,
/// by metadata, in another repository), or one carrying <c>[EventModelRole]</c>, <c>[EventModelTrigger]</c>
/// or <c>[EventModelAct]</c> — and whose name opens with Given, When or Then, which is the keyword shown.
/// A method with a step attribute is a planned step already and is left to that.
/// </para>
/// <para>
/// <b>Types, never values.</b> The text is the method name after its keyword, plus the types the call
/// names: its type arguments and the static types of what it hands an <c>object</c> parameter
/// (<c>When received ShipOrder</c>). Those are compile-time facts; an argument's value is not.
/// </para>
/// </remarks>
internal static class VocabularyCalls
{
    internal sealed class Call
    {
        public string Keyword = "";
        public string Text = "";

        /// <summary>The vocabulary method as <c>Type.Method</c>.</summary>
        public string Grammar = "";

        public int Line;
    }

    private static readonly string[] keywords = { "Given", "When", "Then" };

    private static readonly HashSet<string> vocabularyTypes = new(StringComparer.Ordinal)
    {
        "Bobcat.CritterStack.CritterStackScenario",
        "Bobcat.CritterStack.CritterStackSpec"
    };

    public static List<Call> Read(MethodDeclarationSyntax method, SemanticModel model, CancellationToken ct)
    {
        var calls = new List<Call>();
        SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body is null) return calls;

        foreach (var invocation in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (model.GetOperation(invocation, ct) is not IInvocationOperation operation) continue;

            var target = operation.TargetMethod;
            if (keywordOf(target.Name) is not { } keyword) continue;
            if (!isVocabulary(target)) continue;

            var words = MarkerSpecNaming.PascalCaseToTitle(target.Name.Substring(keyword.Length)).ToLowerInvariant();
            var types = typesNamedBy(operation).ToList();
            var text = string.Join(" ", new[] { words, string.Join(", ", types) }.Where(x => x.Length > 0));

            calls.Add(new Call
            {
                Keyword = keyword,
                Text = text,
                Grammar = target.ContainingType.Name + "." + target.Name,
                Line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            });
        }

        return calls;
    }

    private static string? keywordOf(string name)
        => keywords.FirstOrDefault(k => name.StartsWith(k, StringComparison.Ordinal)
                                        && (name.Length == k.Length || char.IsUpper(name[k.Length])));

    private static bool isVocabulary(IMethodSymbol method)
    {
        var original = (method.ReducedFrom ?? method).OriginalDefinition;
        if (StepAttributes.On(original) is not null) return false;
        if (ProjectedRoles.StatesEventModel(method)) return true;

        for (var type = original.ContainingType; type is not null; type = type.BaseType)
        {
            if (vocabularyTypes.Contains(type.OriginalDefinition.ToDisplayString())) return true;
        }

        return false;
    }

    private static IEnumerable<string> typesNamedBy(IInvocationOperation operation)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var typeArgument in operation.TargetMethod.TypeArguments)
        {
            if (nameOf(typeArgument) is { } name && seen.Add(name)) yield return name;
        }

        foreach (var argument in operation.Arguments)
        {
            if (argument.ArgumentKind == ArgumentKind.DefaultValue) continue;

            var values = argument.Value is IArrayCreationOperation { Initializer: { } initializer }
                         && argument.ArgumentKind == ArgumentKind.ParamArray
                ? initializer.ElementValues
                : (IEnumerable<IOperation>)new[] { argument.Value };

            // Only what an object parameter is handed: a typed parameter's type is the method's, not the call's
            var parameter = argument.Parameter?.Type;
            var takesObject = parameter?.SpecialType == SpecialType.System_Object
                              || parameter is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Object };
            if (!takesObject) continue;

            foreach (var value in values)
            {
                var operand = value is IConversionOperation conversion ? conversion.Operand : value;
                if (operand.Type is { } type && nameOf(type) is { } name && seen.Add(name)) yield return name;
            }
        }
    }

    /// <summary>
    /// The short name of a type a reader would recognise as the application's own: never a type parameter,
    /// a primitive or a framework type. Bobcat's wrappers (<c>Specified&lt;Order&gt;</c>) read as what they wrap.
    /// </summary>
    private static string? nameOf(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || named.TypeKind == TypeKind.Error) return null;
        if (named.SpecialType != SpecialType.None || named.IsAnonymousType || named.IsTupleType) return null;

        var ns = named.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns is "Bobcat" or "Bobcat.CritterStack")
            return named.TypeArguments.Length == 1 ? nameOf(named.TypeArguments[0]) : null;
        if (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)) return null;

        return named.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }
}
