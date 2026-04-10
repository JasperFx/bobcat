using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Gherkin;
using Gherkin.Ast;
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
        if (string.IsNullOrWhiteSpace(content)) return null;

        try
        {
            var parser = new Parser();
            var doc = parser.Parse(new StringReader(content));
            var feature = doc.Feature;
            if (feature == null) return null;

            var info = new FeatureInfo
            {
                Title = feature.Name ?? "",
                FilePath = path,
            };

            foreach (var child in feature.Children)
            {
                if (child is Scenario scenario)
                {
                    var scenarioInfo = new ScenarioInfo
                    {
                        Title = scenario.Name ?? "",
                        Tags = scenario.Tags?.Select(t => t.Name.TrimStart('@')).ToList() ?? new()
                    };

                    string lastKeyword = "Given";
                    foreach (var step in scenario.Steps)
                    {
                        var keyword = step.Keyword.Trim();
                        var resolved = keyword;
                        if (keyword == "And" || keyword == "But" || keyword == "*")
                        {
                            resolved = lastKeyword;
                        }
                        else
                        {
                            lastKeyword = keyword;
                        }

                        var stepInfo = new StepInfo
                        {
                            Keyword = keyword,
                            ResolvedKeyword = resolved,
                            Text = step.Text?.Trim() ?? ""
                        };

                        if (step.Argument is DataTable dataTable)
                        {
                            var rows = dataTable.Rows.ToList();
                            if (rows.Count > 0)
                            {
                                stepInfo.TableHeaders = rows[0].Cells.Select(c => c.Value).ToList();
                                stepInfo.TableRows = rows.Skip(1)
                                    .Select(r => r.Cells.Select(c => c.Value).ToList())
                                    .ToList();
                            }
                        }

                        scenarioInfo.Steps.Add(stepInfo);
                    }

                    info.Scenarios.Add(scenarioInfo);
                }
                // TODO: Handle Background, ScenarioOutline
            }

            return info;
        }
        catch
        {
            return null;
        }
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

        return info;
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

        var info = new StepMethodInfo
        {
            MethodName = method.Name,
            Expression = expression,
            StepKind = kind,
            IsAsync = method.IsAsync || method.ReturnType.Name == "Task",
        };

        // Check for [Table] and [SetVerification]
        foreach (var attr in method.GetAttributes())
        {
            if (attr.AttributeClass?.Name == "TableAttribute")
                info.IsTable = true;
            if (attr.AttributeClass?.Name == "SetVerificationAttribute")
            {
                info.IsSetVerification = true;
                var keyProp = attr.NamedArguments.FirstOrDefault(a => a.Key == "KeyColumns");
                info.SetVerificationKeyColumns = keyProp.Value.Value?.ToString() ?? "";
            }
        }

        // Collect parameters
        foreach (var param in method.Parameters)
        {
            info.Parameters.Add(new ParameterInfo
            {
                Name = param.Name,
                Type = param.Type.ToDisplayString()
            });
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
