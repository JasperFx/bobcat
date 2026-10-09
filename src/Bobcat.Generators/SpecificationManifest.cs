using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bobcat.Generators;

/// <summary>
/// bobcat#449: one <c>SpecificationBindingDescriptor</c> per test of a <c>[BobcatFeature]</c> class — its
/// <c>{Feature}/{Scenario}</c> identity and the command it sends — so the specifications are a model
/// source of their own and an <c>EventModelDefinition</c> needs no <c>LinksToSpecification</c> to be
/// joined to them (jasperfx#995).
/// </summary>
/// <remarks>
/// <para>
/// <b>The command is what the test already says.</b> The first <c>When…</c> call whose first argument
/// is <c>Specify&lt;T&gt;()…</c> or <c>new T(…)</c> names it. A <c>[BobcatSlice]</c> on the test or its
/// class (#324) names the slice outright — <c>SliceType = typeof(…)</c>, or <c>SliceName</c> where no type
/// bears the slice's name — with the <c>Domain</c> that tells modules apart, and the join then uses
/// it instead of the inference. A method's attribute replaces its class's, as it does at run time.
/// </para>
/// <para>
/// <b>Gated on the contract.</b> Emitted only where the compilation can see JasperFx's
/// <c>SpecificationBindingDescriptor</c>; anywhere else there is nothing to emit against.
/// </para>
/// </remarks>
internal static class SpecificationManifest
{
    public const string GateTypeName = "JasperFx.Events.EventModeling.SpecificationBindingDescriptor";
    private const string FeatureAttribute = "Bobcat.BobcatFeatureAttribute";
    private const string SliceAttribute = "Bobcat.BobcatSliceAttribute";

    // netstandard2.0: classes with value equality rather than records, so the pipeline stays incremental
    internal sealed class Binding : IEquatable<Binding>
    {
        public Binding(string identity, string? commandType, string? sliceName, string? domain, string? @namespace)
        {
            Identity = identity;
            CommandType = commandType;
            SliceName = sliceName;
            Domain = domain;
            Namespace = @namespace;
        }

        public string Identity { get; }
        public string? CommandType { get; }
        public string? SliceName { get; }
        public string? Domain { get; }
        public string? Namespace { get; }

        public bool Equals(Binding? other)
            => other is not null && Identity == other.Identity && CommandType == other.CommandType
               && SliceName == other.SliceName && Domain == other.Domain && Namespace == other.Namespace;

        public override bool Equals(object? obj) => Equals(obj as Binding);

        public override int GetHashCode() => Identity.GetHashCode();
    }

    internal sealed class Feature : IEquatable<Feature>
    {
        public Feature(string className, ImmutableArray<Binding> bindings)
        {
            ClassName = className;
            Bindings = bindings;
        }

        public string ClassName { get; }
        public ImmutableArray<Binding> Bindings { get; }

        public bool Equals(Feature? other)
            => other is not null && ClassName == other.ClassName && Bindings.SequenceEqual(other.Bindings);

        public override bool Equals(object? obj) => Equals(obj as Feature);

        public override int GetHashCode() => ClassName.GetHashCode();
    }

    public static bool IsCandidate(SyntaxNode node)
        => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 };

    public static Feature? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var declaration = (ClassDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(declaration, ct) is not INamedTypeSymbol type) return null;

        var feature = attribute(type, FeatureAttribute);
        var classSlice = attribute(type, SliceAttribute);
        if (feature is null && classSlice is null) return null;

        var title = feature?.ConstructorArguments.FirstOrDefault().Value as string;
        var featureTitle = MarkerSpecNaming.FeatureTitle(type.Name, title);

        var bindings = ImmutableArray.CreateBuilder<Binding>();
        foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
        {
            if (!isTest(method)) continue;
            if (ctx.SemanticModel.GetDeclaredSymbol(method, ct) is not IMethodSymbol symbol) continue;

            var identity = featureTitle + "/" + MarkerSpecNaming.ScenarioTitle(method.Identifier.ValueText);
            var explicitSlice = attribute(symbol, SliceAttribute) ?? classSlice;

            var command = inferCommand(method, ctx.SemanticModel, ct);
            var binding = explicitSlice is not null
                ? fromAttribute(identity, command, explicitSlice)
                : new Binding(identity, command, null, null, null);

            if (binding.CommandType is null && binding.SliceName is null) continue;
            bindings.Add(binding);
        }

        return bindings.Count == 0 ? null : new Feature(type.ToDisplayString(), bindings.ToImmutable());
    }

    public static string Emit(IEnumerable<Feature> features)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> bobcat#449: each specification and the command or slice it exercises");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("[assembly: global::Bobcat.SpecificationManifest(typeof(global::Bobcat.Generated.SpecificationBindings))]");
        sb.AppendLine();
        sb.AppendLine("namespace Bobcat.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>The specifications in this assembly as JasperFx specification bindings (jasperfx#995).</summary>");
        sb.AppendLine("    internal static class SpecificationBindings");
        sb.AppendLine("    {");
        sb.AppendLine("        public static global::System.Collections.Generic.IReadOnlyList<global::JasperFx.Events.EventModeling.SpecificationBindingDescriptor> All { get; } =");
        sb.AppendLine("            new global::JasperFx.Events.EventModeling.SpecificationBindingDescriptor[]");
        sb.AppendLine("            {");
        foreach (var binding in features.OrderBy(x => x.ClassName, StringComparer.Ordinal).SelectMany(x => x.Bindings))
        {
            var init = new List<string>();
            if (binding.CommandType is not null)
                init.Add($"CommandType = global::JasperFx.Descriptors.TypeDescriptor.For(typeof(global::{binding.CommandType}))");
            if (binding.SliceName is not null) init.Add($"SliceName = {literal(binding.SliceName)}");
            if (binding.Domain is not null) init.Add($"Domain = {literal(binding.Domain)}");
            if (binding.Namespace is not null) init.Add($"Namespace = {literal(binding.Namespace)}");

            sb.AppendLine($"                new({literal(binding.Identity)}) {{ {string.Join(", ", init)} }},");
        }

        sb.AppendLine("            };");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // SliceType means exactly SliceName = type.Name (#324)
    private static Binding fromAttribute(string identity, string? command, AttributeData data)
    {
        TypedConstant arg(string name) => data.NamedArguments.FirstOrDefault(x => x.Key == name).Value;

        var slice = arg("SliceType").Value is INamedTypeSymbol type ? type.Name : arg("SliceName").Value as string;
        return new Binding(identity, command, slice, arg("Domain").Value as string, null);
    }

    // The first When…(Specify<T>()…) or When…(new T(…)) in the test names the command it sends
    private static string? inferCommand(MethodDeclarationSyntax method, SemanticModel model, CancellationToken ct)
    {
        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = invocation.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                GenericNameSyntax generic => generic.Identifier.ValueText,
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                _ => null
            };

            if (name is null || !name.StartsWith("When", StringComparison.Ordinal)) continue;
            if (invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is not { } argument) continue;

            var type = argument switch
            {
                ObjectCreationExpressionSyntax creation => model.GetTypeInfo(creation, ct).Type,
                _ => specified(argument, model, ct)
            };

            if (type is INamedTypeSymbol named && named.TypeKind != TypeKind.Error) return qualified(named);
        }

        return null;
    }

    // Specify<T>() at the root of a .With(...)/.Check(...) chain
    private static ITypeSymbol? specified(ExpressionSyntax expression, SemanticModel model, CancellationToken ct)
    {
        var current = expression;
        while (current is InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is GenericNameSyntax { Identifier.ValueText: "Specify", TypeArgumentList.Arguments.Count: 1 } generic)
                return model.GetTypeInfo(generic.TypeArgumentList.Arguments[0], ct).Type;

            current = invocation.Expression is MemberAccessExpressionSyntax member ? member.Expression : null;
        }

        return null;
    }

    private static bool isTest(MethodDeclarationSyntax method)
        => method.AttributeLists.SelectMany(x => x.Attributes).Any(a =>
        {
            var name = a.Name.ToString();
            var last = name.Substring(name.LastIndexOf('.') + 1);
            return last is "Fact" or "FactAttribute" or "Theory" or "TheoryAttribute" or "Test" or "TestAttribute";
        });

    private static AttributeData? attribute(ISymbol symbol, string fullName)
        => symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == fullName);

    private static string qualified(INamedTypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");

    private static string literal(string text)
        => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
