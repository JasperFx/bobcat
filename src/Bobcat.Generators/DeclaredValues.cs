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
/// <c>var theAppointmentId = Guid.NewGuid();</c> in a test reads <c>theAppointmentId</c> wherever that
/// value appears in the specification: each such call is intercepted to declare the value under the
/// name it is assigned to (<c>ScenarioValues.Declare</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only where the name is plain from the syntax:</b> a local variable, field or property
/// initialised straight from <c>Guid.NewGuid()</c> or <c>Guid.CreateVersion7()</c>. An assignment
/// later, an argument, or a Guid built any other way is left alone, and is named as before.
/// </para>
/// <para>
/// <b>Only in test code:</b> a type marked <c>[BobcatFeature]</c> (or deriving from one that is), a
/// type declaring a test method, or an abstract type, which in a spec project is a spec's base class.
/// Application code compiled into a test project keeps its Guids anonymous.
/// </para>
/// </remarks>
internal static class DeclaredValues
{
    internal sealed class Declaration
    {
        public string InterceptsLocation = "";
        public string Method = "";
        public string Name = "";
    }

    private static readonly HashSet<string> testAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "FactAttribute", "Theory", "TheoryAttribute", "Test", "TestAttribute",
        "TestMethod", "TestMethodAttribute", "TestCase", "TestCaseAttribute"
    };

    public static bool IsCandidate(SyntaxNode node)
        => node is InvocationExpressionSyntax
           {
               Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "NewGuid" or "CreateVersion7" },
               ArgumentList.Arguments.Count: 0,
               Parent: EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax or PropertyDeclarationSyntax }
           };

    public static Declaration? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        if (ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return null;
        if (!method.IsStatic || method.Parameters.Length != 0
            || method.ContainingType.ToDisplayString() != "System.Guid") return null;

        // An older Bobcat runtime without Declare: generate nothing rather than code that cannot compile
        var values = ctx.SemanticModel.Compilation.GetTypeByMetadataName("Bobcat.Engine.ScenarioValues");
        if (values is null || values.GetMembers("Declare").IsEmpty) return null;

        var name = invocation.Parent!.Parent switch
        {
            VariableDeclaratorSyntax variable => variable.Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            _ => null
        };
        if (name is null) return null;

        var enclosing = ctx.SemanticModel.GetEnclosingSymbol(invocation.SpanStart, ct);
        var type = enclosing as INamedTypeSymbol ?? enclosing?.ContainingType;
        if (type is null || !isTestCode(type)) return null;

#pragma warning disable RSEXPERIMENTAL002
        var location = ctx.SemanticModel.GetInterceptableLocation(invocation, ct);
        if (location is null) return null;

        return new Declaration
        {
            InterceptsLocation = location.GetInterceptsLocationAttributeSyntax(),
            Method = method.Name,
            Name = name
        };
#pragma warning restore RSEXPERIMENTAL002
    }

    private static bool isTestCode(INamedTypeSymbol type)
    {
        // A local inside a lambda or local function still belongs to the type declaring it
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.IsAbstract && current.TypeKind == TypeKind.Class) return true;
            if (current.GetMembers().OfType<IMethodSymbol>()
                .Any(m => m.GetAttributes().Any(a => a.AttributeClass is { } c && testAttributes.Contains(c.Name)))) return true;

            for (var t = current; t is not null; t = t.BaseType)
            {
                if (t.GetAttributes().Any(a => a.AttributeClass?.Name is "BobcatFeatureAttribute" or "BobcatFeature")) return true;
            }
        }

        return false;
    }

    /// <summary>Whether the project opted <c>Bobcat.Generated</c> into interceptors.</summary>
    public static bool InterceptorsEnabled(ParseOptions options)
        => new[] { "InterceptorsNamespaces", "InterceptorsPreviewNamespaces" }
            .Any(key => options.Features.TryGetValue(key, out var namespaces)
                        && namespaces.Split(';', ',').Any(n => n.Trim() == StepInterceptors.Namespace));

    public static string Emit(IReadOnlyList<Declaration> declarations)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("namespace System.Runtime.CompilerServices");
        sb.AppendLine("{");
        sb.AppendLine("    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]");
        sb.AppendLine("    file sealed class InterceptsLocationAttribute : Attribute");
        sb.AppendLine("    {");
        sb.AppendLine("        public InterceptsLocationAttribute(int version, string data) { }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine($"namespace {StepInterceptors.Namespace}");
        sb.AppendLine("{");
        sb.AppendLine("    file static class BobcatDeclaredValues");
        sb.AppendLine("    {");

        for (var i = 0; i < declarations.Count; i++)
        {
            var d = declarations[i];
            sb.AppendLine($"        {d.InterceptsLocation}");
            sb.AppendLine($"        internal static global::System.Guid __BobcatDeclared{i}()");
            sb.AppendLine($"            => global::Bobcat.Engine.ScenarioValues.Declare(global::System.Guid.{d.Method}(), {StepInterceptors.Quote(d.Name)});");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
