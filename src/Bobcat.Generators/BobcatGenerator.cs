using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bobcat.Generators;

[Generator]
public class BobcatGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 1. Collect .feature files
        var featureFiles = context.AdditionalTextsProvider
            .Where(file => file.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
            .Select((file, ct) => ParseFeatureFile(file.Path, file.GetText(ct)?.ToString() ?? ""));

        // 2. Collect fixture classes (any class inheriting from Bobcat.Fixture)
        var fixtureClasses = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => node is ClassDeclarationSyntax cds && cds.BaseList != null,
                transform: (ctx, ct) => ExtractFixtureInfo(ctx, ct))
            .Where(f => f != null)
            .Select((f, _) => f!);

        // 3. Combine features + fixtures
        var combined = featureFiles.Collect()
            .Combine(fixtureClasses.Collect());

        // 4. Generate source
        context.RegisterSourceOutput(combined, (spc, pair) =>
        {
            var features = pair.Left;
            var fixtures = pair.Right;

            foreach (var feature in features)
            {
                if (feature == null) continue;

                var fixture = FindFixture(feature, fixtures);
                if (fixture == null)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.NoMatchingFixture,
                        Microsoft.CodeAnalysis.Location.None,
                        feature.Title));
                    continue;
                }

                try
                {
                    var matched = MatchScenarios(feature, fixture, spc);
                    if (matched == null) continue;

                    var source = CodeEmitter.EmitFeature(feature, fixture, matched);
                    var fileName = CodeEmitter.SanitizeIdentifier(feature.Title) + "_Feature.g.cs";
                    spc.AddSource(fileName, source);
                }
                catch (Exception ex)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.GenerationError,
                        Microsoft.CodeAnalysis.Location.None,
                        feature.Title, ex.Message));
                }
            }
        });
    }

    private static FeatureInfo? ParseFeatureFile(string path, string content)
    {
        return SimpleGherkinParser.Parse(content, path);
    }

    private static FixtureInfo? ExtractFixtureInfo(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)ctx.Node;
        var symbol = ctx.SemanticModel.GetDeclaredSymbol(classDecl, ct) as INamedTypeSymbol;
        if (symbol == null) return null;

        // Check if it inherits from Bobcat.Fixture
        if (!InheritsFrom(symbol, "Bobcat.Fixture")) return null;

        // Don't generate for the base Fixture class itself
        if (symbol.IsAbstract) return null;

        var info = new FixtureInfo
        {
            ClassName = symbol.Name,
            Namespace = symbol.ContainingNamespace.ToDisplayString(),
            FullyQualifiedName = symbol.ToDisplayString(),
        };

        // Check for [FixtureTitle]
        var titleAttr = symbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "FixtureTitleAttribute");
        if (titleAttr != null && titleAttr.ConstructorArguments.Length > 0)
        {
            info.Title = titleAttr.ConstructorArguments[0].Value?.ToString() ?? "";
        }
        else
        {
            // Derive title from class name
            var name = symbol.Name;
            if (name.EndsWith("Fixture"))
                name = name.Substring(0, name.Length - 7);
            info.Title = DeriveTitle(name);
        }

        // Collect step methods
        foreach (var member in symbol.GetMembers().OfType<IMethodSymbol>())
        {
            var stepMethod = ExtractStepMethod(member);
            if (stepMethod != null)
            {
                info.StepMethods.Add(stepMethod);
            }
        }

        // Collect [IncludeGrammars] modules
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name != "IncludeGrammarsAttribute") continue;

            foreach (var arg in attr.ConstructorArguments)
            {
                // params Type[] arrives as an array typed constant
                foreach (var typeConstant in arg.Values)
                {
                    if (typeConstant.Value is INamedTypeSymbol moduleSymbol)
                        info.Modules.Add(ExtractModuleInfo(moduleSymbol));
                }
            }
        }

        return info;
    }

    private static ModuleInfo ExtractModuleInfo(INamedTypeSymbol moduleSymbol)
    {
        var module = new ModuleInfo
        {
            FullyQualifiedName = moduleSymbol.ToDisplayString(),
            IsFixture = InheritsFrom(moduleSymbol, "Bobcat.Fixture"),
        };

        foreach (var member in moduleSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            var stepMethod = ExtractStepMethod(member);
            if (stepMethod != null)
            {
                stepMethod.DeclaringModule = module.FullyQualifiedName;
                module.StepMethods.Add(stepMethod);
            }
        }

        return module;
    }

    private static StepMethodInfo? ExtractStepMethod(IMethodSymbol method)
    {
        string? expression = null;
        string? kind = null;

        foreach (var attr in method.GetAttributes())
        {
            var attrName = attr.AttributeClass?.Name;
            if (attrName == "GivenAttribute") { kind = "Given"; expression = attr.ConstructorArguments[0].Value?.ToString(); }
            else if (attrName == "WhenAttribute") { kind = "When"; expression = attr.ConstructorArguments[0].Value?.ToString(); }
            else if (attrName == "ThenAttribute") { kind = "Then"; expression = attr.ConstructorArguments[0].Value?.ToString(); }
            else if (attrName == "CheckAttribute") { kind = "Check"; expression = attr.ConstructorArguments[0].Value?.ToString(); }
        }

        if (expression == null || kind == null) return null;

        var (returnType, isAwaitable) = UnwrapReturnType(method.ReturnType);

        var info = new StepMethodInfo
        {
            MethodName = method.Name,
            Expression = expression,
            StepKind = kind,
            IsAsync = isAwaitable,
            ReturnType = returnType,
        };

        // Check for [Table], [SetVerification], [DecisionTable], [Approx], [Expected]
        foreach (var attr in method.GetAttributes())
        {
            switch (attr.AttributeClass?.Name)
            {
                case "TableAttribute":
                    info.IsTable = true;
                    break;
                case "SetVerificationAttribute":
                    info.IsSetVerification = true;
                    var keyProp = attr.NamedArguments.FirstOrDefault(a => a.Key == "KeyColumns");
                    info.SetVerificationKeyColumns = keyProp.Value.Value?.ToString() ?? "";
                    break;
                case "DecisionTableAttribute":
                    info.IsDecisionTable = true;
                    break;
                case "ApproxAttribute":
                    if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is double tol)
                        info.ApproxTolerance = tol;
                    break;
                case "WaitForAttribute":
                    if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is int timeout)
                        info.WaitForTimeoutMs = timeout;
                    var pollProp = attr.NamedArguments.FirstOrDefault(a => a.Key == "PollAt");
                    if (pollProp.Value.Value is int poll)
                        info.WaitForPollMs = poll;
                    break;
                case "ExpectedAttribute":
                    var colArg = attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value?.ToString() : null;
                    var colNamed = attr.NamedArguments.FirstOrDefault(a => a.Key == "Column").Value.Value?.ToString();
                    info.ReturnColumn = colArg ?? colNamed;
                    break;
                case "NewScopeAttribute":
                    info.NewScope = true;
                    info.ScopeResourceName ??= NamedString(attr, "Resource");
                    break;
                case "ScopePerRowAttribute":
                    info.ScopePerRow = true;
                    info.ScopeResourceName ??= NamedString(attr, "Resource");
                    break;
            }
        }

        // Collect parameters
        foreach (var param in method.Parameters)
        {
            info.Parameters.Add(ExtractParameter(param));
        }

        // Parse the expression
        try
        {
            info.ParsedExpression = CucumberExpressionParser.Parse(expression);
        }
        catch
        {
            // Will be reported as diagnostic later
        }

        return info;
    }

    /// <summary>
    /// Build the compile-time model for one parameter, deciding up front whether it is
    /// supplied by the Gherkin text/table or resolved from DI.
    ///
    /// The binder rule: <c>IStepContext</c> and explicitly-attributed parameters are always
    /// injected; a parameter whose type cannot be parsed out of a Gherkin cell is treated as
    /// a service and resolved from the per-scenario scope; everything else is a value.
    /// </summary>
    internal static ParameterInfo ExtractParameter(IParameterSymbol param)
    {
        var info = new ParameterInfo
        {
            Name = param.Name,
            Type = param.Type.ToDisplayString(),
            IsOut = param.RefKind == RefKind.Out,
            IsSimpleType = IsSimpleType(param.Type),
        };

        foreach (var attr in param.GetAttributes())
        {
            switch (attr.AttributeClass?.Name)
            {
                case "FromScopedServiceAttribute":
                    info.Binding = ParameterBinding.ScopedService;
                    info.ResourceName = PositionalOrNamedString(attr, "Resource");
                    break;
                case "FromRootServiceAttribute":
                    info.Binding = ParameterBinding.RootService;
                    info.ResourceName = PositionalOrNamedString(attr, "Resource");
                    break;
                case "FromKeyedServicesAttribute":
                    info.Binding = ParameterBinding.KeyedService;
                    info.ServiceKey = attr.ConstructorArguments.Length > 0
                        ? attr.ConstructorArguments[0].Value?.ToString()
                        : null;
                    info.ResourceName = NamedString(attr, "Resource");
                    break;
            }
        }

        if (info.Binding != ParameterBinding.Value)
        {
            info.IsExplicitlyInjected = true;
            return info;
        }

        if (param.Type.ToDisplayString() == "Bobcat.Engine.IStepContext")
        {
            info.Binding = ParameterBinding.StepContext;
            info.IsExplicitlyInjected = true;
        }
        else if (!info.IsSimpleType && param.RefKind != RefKind.Out)
        {
            // A type no Gherkin cell can produce — resolve it from the scenario scope.
            info.Binding = ParameterBinding.ScopedService;
        }

        return info;
    }

    /// <summary>
    /// True for types a Gherkin cell can be converted into: string, primitives, enums,
    /// decimal, Guid, and the date/time types (plus their nullable forms).
    /// </summary>
    internal static bool IsSimpleType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol nullable
            && nullable.IsGenericType
            && nullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
        {
            type = nullable.TypeArguments[0];
        }

        if (type.TypeKind == TypeKind.Enum) return true;

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Char:
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
            case SpecialType.System_String:
            case SpecialType.System_Object:
            case SpecialType.System_DateTime:
                return true;
        }

        switch (type.ToDisplayString())
        {
            case "System.Guid":
            case "System.TimeSpan":
            case "System.DateOnly":
            case "System.TimeOnly":
            case "System.DateTimeOffset":
            case "System.Uri":
                return true;
        }

        return false;
    }

    private static string? PositionalOrNamedString(AttributeData attr, string namedKey)
        => (attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value?.ToString() : null)
           ?? NamedString(attr, namedKey);

    private static string? NamedString(AttributeData attr, string key)
        => attr.NamedArguments.FirstOrDefault(a => a.Key == key).Value.Value?.ToString();

    private static FixtureInfo? FindFixture(FeatureInfo feature, ImmutableArray<FixtureInfo> fixtures)
    {
        return fixtures.FirstOrDefault(f =>
            string.Equals(f.Title, feature.Title, StringComparison.OrdinalIgnoreCase));
    }

    private static List<MatchedScenario>? MatchScenarios(FeatureInfo feature, FixtureInfo fixture, SourceProductionContext spc)
    {
        var matched = new List<MatchedScenario>();
        var hasErrors = false;

        foreach (var scenario in feature.Scenarios)
        {
            var matchedScenario = new MatchedScenario { Scenario = scenario };

            foreach (var step in scenario.Steps)
            {
                var match = StepMatcher.Match(step, fixture);
                if (match == null)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.UnmatchedStep,
                        Microsoft.CodeAnalysis.Location.None,
                        step.Text, fixture.ClassName));
                    hasErrors = true;
                    continue;
                }

                matchedScenario.Steps.Add(new MatchedStep { Step = step, Match = match });
            }

            matched.Add(matchedScenario);
        }

        return hasErrors ? null : matched;
    }

    /// <summary>
    /// Returns the effective return type (Task/ValueTask unwrapped to their argument, or
    /// "void" for void/Task/ValueTask) and whether the method is awaitable.
    /// </summary>
    private static (string ReturnType, bool IsAwaitable) UnwrapReturnType(ITypeSymbol returnType)
    {
        if (returnType.SpecialType == SpecialType.System_Void)
            return ("void", false);

        if (returnType is INamedTypeSymbol named)
        {
            var name = named.Name;
            if (name == "Task" || name == "ValueTask")
            {
                if (named.TypeArguments.Length == 1)
                    return (named.TypeArguments[0].ToDisplayString(), true);
                return ("void", true); // non-generic Task/ValueTask
            }
        }

        return (returnType.ToDisplayString(), false);
    }

    private static bool InheritsFrom(INamedTypeSymbol symbol, string baseTypeName)
    {
        var current = symbol.BaseType;
        while (current != null)
        {
            if (current.ToDisplayString() == baseTypeName)
                return true;
            current = current.BaseType;
        }
        return false;
    }

    private static string DeriveTitle(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1]))
            {
                sb.Append(' ');
            }
            else if (i > 0 && char.IsUpper(name[i]) && i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))
            {
                sb.Append(' ');
            }
            sb.Append(name[i]);
        }
        return sb.ToString();
    }
}

internal static class Diagnostics
{
    public static readonly DiagnosticDescriptor NoMatchingFixture = new(
        "BOBCAT001",
        "No matching fixture",
        "No fixture found for feature '{0}'. Create a fixture class with [FixtureTitle(\"{0}\")] or name it {0}Fixture.",
        "Bobcat",
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor UnmatchedStep = new(
        "BOBCAT002",
        "Unmatched step",
        "Step '{0}' has no matching method in fixture '{1}'",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor GenerationError = new(
        "BOBCAT003",
        "Code generation error",
        "Error generating code for feature '{0}': {1}",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);
}
