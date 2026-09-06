using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bobcat.Generators;

/// <summary>
/// Emits the Microsoft.Testing.Platform entry point for a spec assembly (issue #207), so a
/// consumer needs only package references and <c>.feature</c> files — the same zero-ceremony
/// bar xUnit v3 sets. The generated <c>Main</c> goes through
/// <c>Bobcat.Mtp.BobcatTestApplication.Run</c>, which is what registers the MSBuild extension
/// <c>dotnet test</c> talks to, scans the assembly for generated features and code-first
/// specifications, then calls every <c>[BobcatConfiguration]</c> method the assembly declares.
/// </summary>
/// <remarks>
/// Emission is gated four ways, in order:
/// <list type="number">
/// <item>a type probe for <c>Bobcat.Mtp.BobcatTestApplication</c> — referencing the MTP host
/// package is how a project says "I am a test host" (same pattern as the
/// <c>EventModelSliceDescriptor</c> probe);</item>
/// <item>the <c>BobcatGenerateEntryPoint</c> MSBuild property, surfaced as an analyzer config
/// value — <c>false</c> opts out (opt-out rather than opt-in, because the gates already mean
/// the consumer asked to be a host, and a hand-written <c>Main</c> always wins anyway);</item>
/// <item>the compilation must be an executable — MTP hosts are;</item>
/// <item>the compilation must have no entry point of its own. A hand-written <c>Main</c> is
/// authoritative and the generator emits nothing, which is what keeps every existing consumer
/// compiling unchanged and makes CS0017 impossible.</item>
/// </list>
/// </remarks>
internal static class EntryPointEmitter
{
    /// <summary>The type whose presence in the compilation gates emission.</summary>
    public const string GateTypeName = "Bobcat.Mtp.BobcatTestApplication";

    /// <summary>The analyzer-config key for the opt-out MSBuild property.</summary>
    public const string GenerateEntryPointProperty = "build_property.BobcatGenerateEntryPoint";

    /// <summary>The hint name of the generated file.</summary>
    public const string HintName = "BobcatEntryPoint.g.cs";

    /// <summary>
    /// A <c>[BobcatConfiguration]</c> method found in the compilation. <see cref="Problem"/> is
    /// null when the generated entry point can call it, otherwise it says why not (BOBCAT016).
    /// </summary>
    internal sealed class ConfigurationMethodInfo
    {
        public string ContainingTypeFqn = "";
        public string MethodName = "";
        public string DisplayName = "";
        public string? Problem;
    }

    /// <summary>
    /// Extracts a configuration method from a method declaration carrying attributes, or null
    /// when the method has no <c>[BobcatConfiguration]</c>. Matched by simple attribute name,
    /// like every other Bobcat attribute — the netstandard2.0 generator references no runtime
    /// assembly.
    /// </summary>
    public static ConfigurationMethodInfo? ExtractConfigurationMethod(
        GeneratorSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        var methodDecl = (MethodDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(methodDecl, ct) is not IMethodSymbol symbol) return null;

        var marked = symbol.GetAttributes()
            .Any(a => a.AttributeClass?.Name == "BobcatConfigurationAttribute");
        if (!marked) return null;

        var info = new ConfigurationMethodInfo
        {
            ContainingTypeFqn = symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            MethodName = symbol.Name,
            DisplayName = symbol.ContainingType.ToDisplayString() + "." + symbol.Name,
            Problem = describeProblem(symbol),
        };

        return info;
    }

    private static string? describeProblem(IMethodSymbol symbol)
    {
        if (!symbol.IsStatic) return "it must be static";
        if (!symbol.ReturnsVoid) return "it must return void";
        if (symbol.IsGenericMethod) return "it must not be generic";

        if (symbol.Parameters.Length != 1
            || symbol.Parameters[0].Type.ToDisplayString() != "Bobcat.Runtime.BobcatRunner")
        {
            return "it must take exactly one Bobcat.Runtime.BobcatRunner parameter";
        }

        if (!isReachable(symbol.DeclaredAccessibility)) return "it must be public or internal";

        for (var type = symbol.ContainingType; type != null; type = type.ContainingType)
        {
            if (type.IsGenericType) return "its declaring type must not be generic";
            if (!isReachable(type.DeclaredAccessibility)) return "its declaring type must be public or internal";
        }

        return null;
    }

    private static bool isReachable(Accessibility accessibility)
        => accessibility is Accessibility.Public or Accessibility.Internal
            or Accessibility.ProtectedOrInternal;

    /// <summary>
    /// The generated entry point. Configuration methods are called in a deterministic order —
    /// sorted by declaring type, then method name — so a suite composed from several partial
    /// registrations behaves the same on every build.
    /// </summary>
    public static string EmitSource(IEnumerable<ConfigurationMethodInfo> callableMethods)
    {
        var ordered = callableMethods
            .OrderBy(m => m.ContainingTypeFqn, StringComparer.Ordinal)
            .ThenBy(m => m.MethodName, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace Bobcat.Generated;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// The Microsoft.Testing.Platform entry point for this spec assembly, generated because");
        sb.AppendLine("/// the compilation references Bobcat.Mtp and declares no Main of its own (issue #207).");
        sb.AppendLine("/// Scans this assembly for generated features and code-first specifications, then calls");
        sb.AppendLine("/// every [BobcatConfiguration] method. Hand-write a Main calling");
        sb.AppendLine("/// Bobcat.Mtp.BobcatTestApplication.Run to take over completely — the generator yields");
        sb.AppendLine("/// to it automatically. Set the MSBuild property BobcatGenerateEntryPoint=false to opt");
        sb.AppendLine("/// out entirely.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("internal static class BobcatEntryPoint");
        sb.AppendLine("{");
        sb.AppendLine("    public static global::System.Threading.Tasks.Task<int> Main(string[] args)");
        sb.AppendLine("        => global::Bobcat.Mtp.BobcatTestApplication.Run(args, Configure);");
        sb.AppendLine();
        sb.AppendLine("    internal static void Configure(global::Bobcat.Runtime.BobcatRunner runner)");
        sb.AppendLine("    {");
        sb.AppendLine("        var assembly = typeof(BobcatEntryPoint).Assembly;");
        sb.AppendLine("        runner.ScanForFeatures(assembly);");
        sb.AppendLine("        global::Bobcat.CodeFirst.SpecificationRunnerExtensions.ScanForSpecifications(runner, assembly);");

        foreach (var method in ordered)
        {
            sb.AppendLine($"        {method.ContainingTypeFqn}.{method.MethodName}(runner);");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
