using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bobcat.Generators;

/// <summary>
/// BOBCAT035: <c>[WaitFor]</c> and <c>[Approx]</c> in projected code, where nothing reads them.
/// </summary>
/// <remarks>
/// <para>
/// Both are modifiers the Gherkin generator applies when it emits the code for a step it bound: a
/// poll loop around the call, a tolerance on the comparison of what it returned. A C# test calls the
/// helper itself, there is no emitted code around the call, and so the attribute did nothing at all.
/// A helper carrying <c>[WaitFor(5000)]</c> compiled, ran once, and reported its first answer,
/// which reads exactly like a poll that happened to succeed straight away. That's the reason this is a
/// diagnostic rather than a line in the docs.
/// </para>
/// <para>
/// Two places count as projected code: a call site the step interceptors already see (a C# call to a
/// step helper, wherever the helper lives), and a method of a projected spec itself (a
/// <c>[BobcatFeature]</c> or <c>[BobcatSlice]</c> class, or a <c>[BobcatSpec]</c> /
/// <c>[BobcatScenario]</c> test). The second excludes step helpers, because the first already covers
/// any of them that are called, and an uncalled one may well be a Gherkin binding.
/// </para>
/// </remarks>
internal static class GherkinOnlyAttributes
{
    private static readonly string[] names = ["WaitFor", "Approx"];

    /// <summary>
    /// The short names (<c>WaitFor</c>, <c>Approx</c>) of the Gherkin-only attributes on
    /// <paramref name="method"/> or any of its parameters, each once, in that order.
    /// </summary>
    public static IEnumerable<string> On(IMethodSymbol method)
    {
        var found = new HashSet<string>();

        foreach (var attribute in method.GetAttributes()
                     .Concat(method.Parameters.SelectMany(p => p.GetAttributes())))
        {
            if (nameOf(attribute.AttributeClass) is { } name) found.Add(name);
        }

        return names.Where(found.Contains);
    }

    private static string? nameOf(INamedTypeSymbol? attributeClass)
    {
        if (attributeClass is null) return null;
        if (attributeClass.ContainingNamespace?.ToDisplayString() != "Bobcat") return null;

        return attributeClass.Name switch
        {
            "WaitForAttribute" => "WaitFor",
            "ApproxAttribute" => "Approx",
            _ => null
        };
    }

    /// <summary>The fix each attribute's warning points at.</summary>
    public static string ReplacementFor(string attribute) => attribute == "WaitFor"
        ? "poll with SpecAssert.EventuallyAsync(...) inside the method instead"
        : "pass the tolerance to the check instead, SpecAssert.Check(name, actual, expected, tolerance: ...)";

    /// <summary>One finding: where to report it, and the message.</summary>
    internal sealed class Finding(Location location, string member, string attribute)
    {
        public Location Location { get; } = location;
        public string Member { get; } = member;
        public string Attribute { get; } = attribute;
    }

    /// <summary>A method declaration with an attribute list that might hold one of the two.</summary>
    public static bool IsDeclarationCandidate(SyntaxNode node)
        => node is MethodDeclarationSyntax method
           && attributesOf(method).Any(a => names.Any(n => isWritten(a, n)));

    private static IEnumerable<AttributeSyntax> attributesOf(MethodDeclarationSyntax method)
        => method.AttributeLists
            .Concat(method.ParameterList.Parameters.SelectMany(p => p.AttributeLists))
            .SelectMany(l => l.Attributes);

    private static bool isWritten(AttributeSyntax attribute, string name)
    {
        var written = attribute.Name.ToString();
        return written.EndsWith(name) || written.EndsWith(name + "Attribute");
    }

    /// <summary>
    /// The findings for a method of a projected spec itself. Empty for anything else, and for a step
    /// helper (see the remarks on the class).
    /// </summary>
    public static Finding[]? CheckDeclaration(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is not MethodDeclarationSyntax declaration) return null;
        if (ctx.SemanticModel.GetDeclaredSymbol(declaration, ct) is not IMethodSymbol method) return null;

        if (StepAttributes.On(method) is not null) return null;
        if (!isProjectedSpec(method)) return null;

        var findings = On(method)
            .Select(name => new Finding(locationOf(declaration, name), method.Name, name))
            .ToArray();

        return findings.Length == 0 ? null : findings;
    }

    private static bool isProjectedSpec(IMethodSymbol method)
    {
        if (method.GetAttributes().Any(a => derivesFrom(a.AttributeClass, "BobcatSpecAttribute", "BobcatScenarioAttribute")))
            return true;

        for (var type = method.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.GetAttributes().Any(a => derivesFrom(a.AttributeClass, "BobcatFeatureAttribute", "BobcatSliceAttribute")))
                return true;
        }

        return false;
    }

    private static bool derivesFrom(INamedTypeSymbol? attributeClass, params string[] typeNames)
    {
        for (var type = attributeClass; type is not null; type = type.BaseType)
        {
            if (typeNames.Contains(type.Name)) return true;
        }

        return false;
    }

    private static Location locationOf(MethodDeclarationSyntax declaration, string name)
    {
        foreach (var attribute in attributesOf(declaration))
        {
            if (isWritten(attribute, name)) return attribute.GetLocation();
        }

        return declaration.Identifier.GetLocation();
    }
}
