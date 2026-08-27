using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bobcat.Generators;

/// <summary>
/// Issue #170: what a code-first specification contributes to the Event Model. A
/// <c>[Scenario]</c> method on a <c>Specification</c> subclass builds a runtime-constructed
/// <c>FeatureDefinition</c> the Gherkin pipeline never sees, so a team authoring specs in C#
/// was getting no event model at all — and none of the <c>Specifications</c> bindings that
/// drive drift colouring. This reads the same declarations Roslyn-side, from the compilation
/// the generator already walks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Roslyn-side rather than a runtime-contributed source, deliberately.</b> Compile-time
/// extraction keeps identity stamping (<c>{Feature}/{Scenario}</c>, via
/// <see cref="CodeFirstNaming"/>) identical for both authoring styles, which is what lets run
/// evidence join the same way whichever way the spec was written.
/// </para>
/// <para>
/// <b>Roles come from the typed-step convention.</b> Gherkin resolves <c>{command}</c> and
/// friends from step text; a C# scenario body names its types through the typed event-sourcing
/// steps (issue #105's shared surface): <c>GivenEvents&lt;T&gt;</c>/<c>GivenNoEvents&lt;T&gt;</c>
/// stamp the aggregate, <c>WhenCommand&lt;T&gt;</c> the aggregate plus the argument's static
/// type as the command, <c>ThenEvents(...)</c> the argument types as events,
/// <c>ThenDocument&lt;T&gt;</c> the read model, <c>ThenMessagesSent&lt;T&gt;</c> the message.
/// Matched by name, gated on the target being declared on a <c>Bobcat.Fixture</c> subclass so
/// an unrelated service method named <c>WhenCommand</c> never stamps a phantom role. Lambda
/// bodies count — that is where <c>Host&lt;TFixture&gt;()</c>-borrowed steps live.
/// </para>
/// <para>
/// Arrange-event arguments to <c>GivenEvents</c> are deliberately not stamped: the Gherkin
/// path's <c>Given events for {aggregate}</c> resolves only the aggregate (its event rows are a
/// runtime lookup), and the two authoring styles must produce the same shape from the same spec.
/// </para>
/// </remarks>
internal static class CodeFirstSpecs
{
    private const string SpecificationBase = "Bobcat.CodeFirst.Specification";
    private const string ScenarioAttributeName = "Bobcat.CodeFirst.ScenarioAttribute";
    private const string FixtureTitleAttributeName = "Bobcat.FixtureTitleAttribute";
    private const string FixtureBase = "Bobcat.Fixture";

    /// <summary>One Specification subclass: its derived feature title and its scenarios.</summary>
    internal sealed class SpecInfo
    {
        public string FeatureTitle = "";
        public readonly List<ScenarioInfo> Scenarios = new();
    }

    internal sealed class ScenarioInfo
    {
        public string Title = "";
        public string[] Tags = Array.Empty<string>();

        /// <summary>An empty method body: declared but unbound — the pending-specification hotspot.</summary>
        public bool IsPending;

        /// <summary>The last WhenCommand's command type — the act, same rule as the Gherkin path.</summary>
        public string? ActCommand;

        /// <summary>(role word, global::-qualified type), in body order.</summary>
        public readonly List<(string Role, string Type)> Roles = new();
    }

    public static SpecInfo? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol symbol) return null;
        if (symbol.IsAbstract) return null;
        if (!derivesFrom(symbol, SpecificationBase)) return null;

        // A partial class surfaces once per declaration; only the first one speaks for the type,
        // so a slice is never double-counted.
        if (symbol.DeclaringSyntaxReferences.Length > 1 &&
            !ReferenceEquals(symbol.DeclaringSyntaxReferences[0].GetSyntax(ct), classDecl))
        {
            return null;
        }

        var spec = new SpecInfo { FeatureTitle = featureTitle(symbol) };

        // Base classes count — the runtime collects their [Scenario] methods into this feature
        // too. A base declared only in metadata still contributes identity and tags; roles need
        // a body in this semantic model's tree.
        for (var type = symbol;
             type != null && type.SpecialType != SpecialType.System_Object
                          && type.ToDisplayString() != SpecificationBase;
             type = type.BaseType)
        {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                var attribute = method.GetAttributes().FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString() == ScenarioAttributeName);
                if (attribute == null) continue;

                spec.Scenarios.Add(scenario(ctx, method, attribute, ct));
            }
        }

        return spec.Scenarios.Count == 0 ? null : spec;
    }

    private static ScenarioInfo scenario(
        GeneratorSyntaxContext ctx, IMethodSymbol method, AttributeData attribute, CancellationToken ct)
    {
        var attributeTitle = attribute.ConstructorArguments.Length > 0
            ? attribute.ConstructorArguments[0].Value as string
            : null;

        var tags = attribute.NamedArguments
            .Where(kv => kv.Key == "Tags")
            .SelectMany(kv => kv.Value.Values)
            .Select(v => v.Value as string)
            .Where(v => v != null)
            .Select(v => v!)
            .ToArray();

        var info = new ScenarioInfo
        {
            Title = CodeFirstNaming.ScenarioTitle(method.Name, attributeTitle),
            Tags = tags
        };

        var syntax = method.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(ct))
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();

        // Metadata-only (a base spec from a package): identity and tags still count.
        if (syntax == null) return info;

        if ((syntax.Body?.Statements.Count ?? 0) == 0 && syntax.ExpressionBody == null)
        {
            info.IsPending = true;
            return info;
        }

        // Only a body in this semantic model's tree can be interrogated; the other half of a
        // partial class degrades to "no roles" rather than juggling models.
        if (syntax.SyntaxTree != ctx.SemanticModel.SyntaxTree) return info;

        foreach (var invocation in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol target) continue;
            if (!derivesFrom(target.ContainingType, FixtureBase)) continue;

            switch (target.Name)
            {
                case "GivenEvents":
                case "GivenNoEvents":
                    addTypeArgument(info, "aggregate", target);
                    break;

                case "WhenCommand":
                    addTypeArgument(info, "aggregate", target);
                    var command = argumentType(ctx, invocation, 0, ct);
                    if (command != null)
                    {
                        info.Roles.Add(("command", command));
                        info.ActCommand = command;
                    }

                    break;

                case "ThenEvents":
                    foreach (var argument in invocation.ArgumentList.Arguments)
                    {
                        var @event = qualified(ctx.SemanticModel.GetTypeInfo(argument.Expression, ct).Type);
                        if (@event != null) info.Roles.Add(("event", @event));
                    }

                    break;

                case "ThenDocument":
                    addTypeArgument(info, "readmodel", target);
                    break;

                case "ThenMessagesSent":
                    addTypeArgument(info, "message", target);
                    break;
            }
        }

        return info;
    }

    private static void addTypeArgument(ScenarioInfo info, string role, IMethodSymbol target)
    {
        if (target.TypeArguments.Length == 0) return;
        var type = qualified(target.TypeArguments[0]);
        if (type != null) info.Roles.Add((role, type));
    }

    private static string? argumentType(
        GeneratorSyntaxContext ctx, InvocationExpressionSyntax invocation, int index, CancellationToken ct)
    {
        if (invocation.ArgumentList.Arguments.Count <= index) return null;
        return qualified(ctx.SemanticModel.GetTypeInfo(invocation.ArgumentList.Arguments[index].Expression, ct).Type);
    }

    /// <summary>
    /// The <c>global::</c>-qualified name a role travels as — the same shape the Gherkin path's
    /// resolved captures use, so one type contributed by both authoring styles deduplicates.
    /// Null for anything that is not a concrete named type: an error type from a broken build,
    /// or a plain <c>object</c> (a variable the model cannot see through), which would stamp a
    /// meaningless role.
    /// </summary>
    private static string? qualified(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named) return null;
        if (named.TypeKind == TypeKind.Error || named.SpecialType == SpecialType.System_Object) return null;
        return named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static bool derivesFrom(INamedTypeSymbol? type, string fullName)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            if (t.ToDisplayString() == fullName) return true;
        }

        return false;
    }

    private static string featureTitle(INamedTypeSymbol symbol)
    {
        var attribute = symbol.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == FixtureTitleAttributeName);
        var attributeTitle = attribute is { ConstructorArguments.Length: > 0 }
            ? attribute.ConstructorArguments[0].Value as string
            : null;

        return CodeFirstNaming.FeatureTitle(symbol.Name, attributeTitle);
    }
}
