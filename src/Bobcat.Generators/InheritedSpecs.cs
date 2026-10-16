using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bobcat.Generators;

/// <summary>
/// bobcat#600: the specifications a concrete test class inherits from a shared base, each under the
/// subclass's own identity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second reader.</b> A shared test base — Wolverine's <c>TransportCompliance&lt;T&gt;</c>,
/// subclassed bodyless by every transport — declares its tests once, often in another assembly, and
/// the compilation that RUNS them has no test method syntax at all. <see cref="MarkerCommentSpecs"/>
/// reads methods in source, so it bound nothing here, and the subclasses had no listing, no preview,
/// no run by identity and no BOBCAT036.
/// </para>
/// <para>
/// <b>Read from symbols, so a base in a referenced assembly works.</b> The test attributes are
/// matched by name, as everywhere in this generator, but on the attribute class and its base classes,
/// because metadata gives the class rather than the spelling a source file used.
/// </para>
/// <para>
/// <b>Bindings only.</b> Marker comments exist only where the body is written, so the steps stay
/// registered under the base's identity, and the binding says so (<c>DeclaredSteps.Inherit</c>). The
/// Event Model is not fed from here: a spec's slice binding and roles are read off its body's syntax.
/// </para>
/// </remarks>
internal static class InheritedSpecs
{
    internal static bool IsCandidate(SyntaxNode node)
        => node is ClassDeclarationSyntax { BaseList: not null } declaration
           && !declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword) || m.IsKind(SyntaxKind.StaticKeyword));

    public static MarkerCommentSpecs.MarkedSpec? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var declaration = (ClassDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol symbol) return null;

        // A runner cannot instantiate an open generic or an abstract class (a partial part may say so).
        if (symbol.IsAbstract || symbol.IsStatic || symbol.TypeParameters.Length > 0) return null;

        // One answer per class, however many partial declarations it has.
        var first = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (first is not null && (first.SyntaxTree != declaration.SyntaxTree || first.Span != declaration.Span)) return null;

        var compilation = ctx.SemanticModel.Compilation;
        var featuresRecord = MarkerCommentSpecs.RecordsFeaturesAssemblyWide(compilation);

        var spec = new MarkerCommentSpecs.MarkedSpec
        {
            FeatureTitle = MarkerSpecNaming.FeatureTitle(symbol.Name, featureTitleOf(symbol, out var marked)),
            TestClass = MarkerCommentSpecs.FilterNameOf(symbol)
        };

        // The class's own methods — and every more derived class's, as the walk climbs — hide a base
        // method of the same name. A test the class declares itself is MarkerCommentSpecs' to bind.
        var hidden = new HashSet<string>(symbol.GetMembers().OfType<IMethodSymbol>().Select(m => m.Name));
        var classOpens = opensRecording(symbol);

        for (var type = symbol.BaseType; type is not null && type.SpecialType != SpecialType.System_Object; type = type.BaseType)
        {
            ct.ThrowIfCancellationRequested();

            classOpens |= opensRecording(type);
            var baseTitle = MarkerSpecNaming.FeatureTitle(type.Name, featureTitleOf(type, out var baseMarked));

            var contributed = false;
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.MethodKind != MethodKind.Ordinary || method.IsAbstract || hidden.Contains(method.Name)) continue;
                if (!hasAttribute(method, MarkerCommentSpecs.TestMethodAttributeNames)) continue;

                var scenario = new MarkerCommentSpecs.MarkedScenario
                {
                    Title = MarkerSpecNaming.ScenarioTitle(method.Name, explicitNameOf(method)),
                    TestMethod = method.Name,
                    Where = declaration.Identifier.GetLocation(),
                    OpensRecording = classOpens
                                     || hasAttribute(method, MarkerCommentSpecs.ScenarioOpeningAttributes)
                                     || (featuresRecord && (marked || baseMarked)),
                    Rows = rowsOf(method, compilation, ct)
                };

                // Bound only when it records — the rule MarkerCommentSpecs.Binds applies.
                if (!scenario.OpensRecording) continue;

                scenario.InheritedFrom = MarkerSpecNaming.Identity(baseTitle, scenario.Title);
                spec.Scenarios.Add(scenario);
                contributed = true;
            }

            if (contributed && !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly))
            {
                var module = type.IsGenericType ? type.ConstructUnboundGenericType() : type;
                spec.BaseModules.Add(module.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }

            foreach (var member in type.GetMembers().OfType<IMethodSymbol>()) hidden.Add(member.Name);
        }

        return spec.Scenarios.Count > 0 ? spec : null;
    }

    /// <summary>
    /// bobcat#600: a test method overriding an inherited <c>[BobcatSpec]</c>/<c>[BobcatTheory]</c> with a
    /// different test attribute (<c>[Fact(Skip = "…")]</c>). xUnit inherits the base's attribute, so the
    /// override carries two Fact-derived attributes and fails at run time. Returns the location to report.
    /// </summary>
    public static Location? SkipOverride(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var method = (MethodDeclarationSyntax)ctx.Node;
        if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword))) return null;
        if (ctx.SemanticModel.GetDeclaredSymbol(method, ct) is not IMethodSymbol symbol) return null;

        var own = symbol.GetAttributes().Select(a => a.AttributeClass).Where(x => x is not null).ToList();
        if (!own.Any(x => namedIn(x!, MarkerCommentSpecs.TestMethodAttributeNames))) return null;
        if (own.Any(x => namedIn(x!, specAttributes))) return null;

        for (var overridden = symbol.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
        {
            if (hasAttribute(overridden, specAttributes)) return method.Identifier.GetLocation();
        }

        return null;
    }

    private static readonly string[] specAttributes =
        { MarkerCommentSpecs.SpecAttributeName, MarkerCommentSpecs.TheoryAttributeName };

    private static bool opensRecording(INamedTypeSymbol type)
        => type.GetAttributes().Any(a => a.AttributeClass is { } c && namedIn(c, new[] { "BobcatScenario" }));

    private static bool hasAttribute(ISymbol symbol, IEnumerable<string> shortNames)
        => symbol.GetAttributes().Any(a => a.AttributeClass is { } c && namedIn(c, shortNames));

    /// <summary>The attribute class, or one it derives from, has one of these short names.</summary>
    private static bool namedIn(INamedTypeSymbol attributeClass, IEnumerable<string> shortNames)
    {
        for (var type = attributeClass; type is not null; type = type.BaseType)
        {
            if (shortNames.Contains(MarkerCommentSpecs.ShortName(type.Name))) return true;
        }

        return false;
    }

    /// <summary><c>[BobcatFeature]</c>'s title on the class itself, never inherited — the runtime's rule.</summary>
    private static string? featureTitleOf(INamedTypeSymbol type, out bool marked)
    {
        marked = false;
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass is null
                || MarkerCommentSpecs.ShortName(attribute.AttributeClass.Name) != MarkerCommentSpecs.AttributeName) continue;

            marked = true;
            return attribute.ConstructorArguments.FirstOrDefault().Value as string;
        }

        return null;
    }

    /// <summary><c>Name = "…"</c> on the method's <c>[BobcatSpec]</c>/<c>[BobcatTheory]</c> (bobcat#489).</summary>
    private static string? explicitNameOf(IMethodSymbol method)
        => method.GetAttributes()
            .Where(a => a.AttributeClass is { } c && namedIn(c, specAttributes))
            .SelectMany(a => a.NamedArguments)
            .Where(pair => pair.Key == "Name")
            .Select(pair => pair.Value.Value as string)
            .FirstOrDefault(name => name is { } value && value.Trim().Length > 0);

    /// <summary>
    /// The method's constant rows (bobcat#569) when its source is in this compilation. A base read from
    /// metadata has no syntax to read them from, so its rows are left to the run, like any row only a
    /// run produces.
    /// </summary>
    private static IReadOnlyList<SpecRowConstants.Row> rowsOf(IMethodSymbol method, Compilation compilation, CancellationToken ct)
    {
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax(ct) is MethodDeclarationSyntax syntax
                && compilation.ContainsSyntaxTree(syntax.SyntaxTree))
            {
                return SpecRowConstants.Read(syntax, compilation.GetSemanticModel(syntax.SyntaxTree), ct);
            }
        }

        return System.Array.Empty<SpecRowConstants.Row>();
    }
}
