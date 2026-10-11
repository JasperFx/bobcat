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
            : this(identity, commandType, sliceName, domain, @namespace, ImmutableArray<string>.Empty)
        {
        }

        public Binding(string identity, string? commandType, string? sliceName, string? domain, string? @namespace,
            ImmutableArray<string> resolvedTypes)
        {
            Identity = identity;
            CommandType = commandType;
            SliceName = sliceName;
            Domain = domain;
            Namespace = @namespace;
            ResolvedTypes = resolvedTypes;
        }

        public string Identity { get; }
        public string? CommandType { get; }
        public string? SliceName { get; }
        public string? Domain { get; }
        public string? Namespace { get; }

        /// <summary>
        /// Every type the test's role-stamped calls named, global::-qualified, in call order (bobcat#462) —
        /// the same list the slice's <c>SpecificationDescriptor</c> carries, and #107's join target.
        /// </summary>
        public ImmutableArray<string> ResolvedTypes { get; }

        public bool Equals(Binding? other)
            => other is not null && Identity == other.Identity && CommandType == other.CommandType
               && SliceName == other.SliceName && Domain == other.Domain && Namespace == other.Namespace
               && ResolvedTypes.SequenceEqual(other.ResolvedTypes);

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
            // A method attribute stating only TriggeredBy (bobcat#463) says nothing about WHICH slice, so it
            // keeps the class's binding rather than replacing it with none — as the Event Model fold does
            var methodSlice = attribute(symbol, SliceAttribute);
            var explicitSlice = methodSlice is not null && !onlyTriggeredBy(methodSlice) ? methodSlice : classSlice;

            // bobcat#462: a role-stamped act names the command by contract; the name-based inference is
            // the fallback for a vocabulary that declares no roles
            var roles = ProjectedRoles.Read(method, ctx.SemanticModel, ct);
            var command = unqualified(roles.Act) ?? inferCommand(method, ctx.SemanticModel, ct);
            var resolved = roles.ResolvedTypes().ToImmutableArray();
            var binding = explicitSlice is not null
                ? fromAttribute(identity, command, explicitSlice, resolved)
                : new Binding(identity, command, null, null, null, resolved);

            if (binding.CommandType is null && binding.SliceName is null) continue;
            bindings.Add(binding);
        }

        return bindings.Count == 0 ? null : new Feature(type.ToDisplayString(), bindings.ToImmutable());
    }

    public static string Emit(IEnumerable<Feature> features) => Emit(features, carriesResolvedTypes: false);

    /// <param name="carriesResolvedTypes">
    /// Whether the compilation's <c>SpecificationBindingDescriptor</c> has <c>ResolvedTypes</c> — a
    /// consumer on an older JasperFx has the descriptor without it, and must still compile.
    /// </param>
    public static string Emit(IEnumerable<Feature> features, bool carriesResolvedTypes)
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
            if (carriesResolvedTypes && binding.ResolvedTypes.Length > 0)
            {
                var types = binding.ResolvedTypes.Select(x => $"global::JasperFx.Descriptors.TypeDescriptor.For(typeof({x}))");
                init.Add($"ResolvedTypes = new global::JasperFx.Descriptors.TypeDescriptor[] {{ {string.Join(", ", types)} }}");
            }

            sb.AppendLine($"                new({literal(binding.Identity)}) {{ {string.Join(", ", init)} }},");
        }

        sb.AppendLine("            };");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // SliceType means exactly SliceName = type.Name (#324)
    private static Binding fromAttribute(string identity, string? command, AttributeData data, ImmutableArray<string> resolved)
    {
        TypedConstant arg(string name) => data.NamedArguments.FirstOrDefault(x => x.Key == name).Value;

        var slice = arg("SliceType").Value is INamedTypeSymbol type ? type.Name : arg("SliceName").Value as string;
        return new Binding(identity, command, slice, arg("Domain").Value as string, null, resolved);
    }

    private static bool onlyTriggeredBy(AttributeData data)
        => data.NamedArguments.Length > 0 && data.NamedArguments.All(x => x.Key == "TriggeredBy");

    // The manifest's CommandType is written without global:: and emitted with it
    private static string? unqualified(string? type)
        => type is not null && type.StartsWith("global::", StringComparison.Ordinal) ? type.Substring("global::".Length) : type;

    // The LAST When…(Specify<T>()…) or When…(new T(…)) in the test names the command it sends (bobcat#464).
    // A spec may arrange state by sending earlier commands -- an HTTP slice that has to WhenPosted a create
    // before the act it is about has no GivenEvents shortcut -- so "first wins" would bind it to the arrange.
    // The Gherkin arm's EventModelEmitter.actCommandOf takes the last one for the same reason.
    private static string? inferCommand(MethodDeclarationSyntax method, SemanticModel model, CancellationToken ct)
    {
        string? command = null;
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

            // The last When names the command; one generated code cannot write typeof(…) of — a private
            // or file-local record — names none, rather than handing the act to an earlier When
            if (type is INamedTypeSymbol named && named.TypeKind != TypeKind.Error)
                command = ProjectedRoles.IsNameable(named, model.Compilation) ? qualified(named) : null;
        }

        return command;
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
