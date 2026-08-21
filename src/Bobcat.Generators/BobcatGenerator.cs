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
            .Select((file, ct) => parseFeatureFile(file.Path, file.GetText(ct)?.ToString() ?? ""));

        // 2. Collect fixture classes (any class inheriting from Bobcat.Fixture)
        var fixtureClasses = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => node is ClassDeclarationSyntax cds && cds.BaseList != null,
                transform: (ctx, ct) => extractFixtureInfo(ctx, ct))
            .Where(f => f != null)
            .Select((f, _) => f!);

        // 3. Collect [TableGrammar] classes — Before-once/per-row/After-once envelopes
        var tableGrammars = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => node is ClassDeclarationSyntax cds && cds.AttributeLists.Count > 0,
                transform: (ctx, ct) => extractTableGrammar(ctx, ct))
            .Where(g => g != null)
            .Select((g, _) => g!);

        // 4. Combine features + fixtures + table grammars + the compilation (type-name captures
        //    such as {aggregate} are resolved against it — see resolveTypeCaptures).
        var combined = featureFiles.Collect()
            .Combine(fixtureClasses.Collect())
            .Combine(tableGrammars.Collect())
            .Combine(context.CompilationProvider);

        // 5. Generate source
        context.RegisterSourceOutput(combined, (spc, pair) =>
        {
            var features = pair.Left.Left.Left;
            var fixtures = pair.Left.Left.Right;
            var grammars = pair.Left.Right;
            var resolver = new TypeNameResolver(pair.Right);

            foreach (var fixture in fixtures)
            {
                foreach (var hidden in fixture.HiddenSteps)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.StepHidesBaseStep, Microsoft.CodeAnalysis.Location.None,
                        hidden.Expression, hidden.DeclaringType, hidden.HiddenType));
                }
            }

            foreach (var feature in features)
            {
                if (feature == null) continue;

                var fixture = findFixture(feature, fixtures);
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
                    if (!validateHooks(fixture, spc)) continue;

                    var matched = matchScenarios(feature, fixture, grammars, resolver, spc);
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

    private static FeatureInfo? parseFeatureFile(string path, string content)
    {
        return SimpleGherkinParser.Parse(content, path);
    }

    private static FixtureInfo? extractFixtureInfo(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)ctx.Node;
        var symbol = ctx.SemanticModel.GetDeclaredSymbol(classDecl, ct) as INamedTypeSymbol;
        if (symbol == null) return null;

        // Check if it inherits from Bobcat.Fixture
        if (!inheritsFrom(symbol, "Bobcat.Fixture")) return null;

        // Don't generate for the base Fixture class itself
        if (symbol.IsAbstract) return null;

        var info = new FixtureInfo
        {
            ClassName = symbol.Name,
            Namespace = symbol.ContainingNamespace.ToDisplayString(),
            FullyQualifiedName = qualified(symbol),
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
            info.Title = deriveTitle(name);
        }

        // Collect step methods and lifecycle hooks — the fixture's own and its base classes' up
        // to Bobcat.Fixture, most-derived first.
        collectStepsAndHooks(symbol, info.StepMethods, info.Hooks, info.HiddenSteps);

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
                        info.Modules.Add(extractModuleInfo(moduleSymbol));
                }
            }
        }

        return info;
    }

    private static ModuleInfo extractModuleInfo(INamedTypeSymbol moduleSymbol)
    {
        var module = new ModuleInfo
        {
            FullyQualifiedName = qualified(moduleSymbol),
            IsFixture = inheritsFrom(moduleSymbol, "Bobcat.Fixture"),
        };

        // A module's steps are inherited the same way a fixture's are, so a shipped grammar base
        // class composes in through [IncludeGrammars] via an empty subclass.
        collectStepsAndHooks(moduleSymbol, module.StepMethods, hooks: null, hidden: null);
        foreach (var stepMethod in module.StepMethods)
            stepMethod.DeclaringModule = module.FullyQualifiedName;

        return module;
    }

    /// <summary>
    /// Walk a fixture (or grammar module) and its base classes, stopping at <c>Bobcat.Fixture</c> /
    /// <c>object</c>, collecting step methods and lifecycle hooks. <b>Most-derived wins</b>: a
    /// method overridden or hidden by a more-derived declaration is skipped by signature, and a
    /// step whose keyword and expression already came from a more-derived class is skipped (and
    /// recorded in <paramref name="hidden"/> so the generator can say so). Before-hooks are
    /// ordered base-first and After-hooks derived-first, the way constructors and disposers nest.
    /// </summary>
    private static void collectStepsAndHooks(INamedTypeSymbol symbol, List<StepMethodInfo> steps,
        List<HookMethodInfo>? hooks, List<HiddenStepInfo>? hidden)
    {
        var seenSignatures = new HashSet<string>(StringComparer.Ordinal);
        var seenSteps = new Dictionary<string, string>(StringComparer.Ordinal); // step key → declaring type
        var levels = new List<List<HookMethodInfo>>();

        for (var type = symbol; type != null && !isDiscoveryRoot(type); type = type.BaseType)
        {
            var levelHooks = new List<HookMethodInfo>();

            foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (member.MethodKind != MethodKind.Ordinary) continue;
                if (!seenSignatures.Add(signatureOf(member))) continue; // overridden/hidden further down

                var stepMethod = extractStepMethod(member);
                if (stepMethod != null)
                {
                    var key = stepKey(stepMethod);
                    if (seenSteps.TryGetValue(key, out var winner))
                    {
                        hidden?.Add(new HiddenStepInfo
                        {
                            Expression = stepMethod.Expression,
                            DeclaringType = winner,
                            HiddenType = type.ToDisplayString(),
                        });
                        continue;
                    }

                    seenSteps[key] = type.ToDisplayString();
                    steps.Add(stepMethod);
                    continue;
                }

                if (hooks == null) continue;

                var hook = extractHook(member);
                if (hook != null) levelHooks.Add(hook);
            }

            levels.Add(levelHooks);
        }

        if (hooks == null) return;

        // levels[0] is the most-derived class. Before* run base-first, After* derived-first.
        for (var i = levels.Count - 1; i >= 0; i--)
            hooks.AddRange(levels[i].Where(h => h.Kind == HookKind.BeforeEach || h.Kind == HookKind.BeforeAll));
        for (var i = 0; i < levels.Count; i++)
            hooks.AddRange(levels[i].Where(h => h.Kind == HookKind.AfterEach || h.Kind == HookKind.AfterAll));
    }

    private static bool isDiscoveryRoot(INamedTypeSymbol type)
        => type.SpecialType == SpecialType.System_Object || type.ToDisplayString() == "Bobcat.Fixture";

    /// <summary>Name + parameter types, so an override or a <c>new</c> hiding is recognised across levels.</summary>
    private static string signatureOf(IMethodSymbol method)
        => method.Name + "(" + string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString())) + ")";

    /// <summary>Keyword (Check counts as Then) + expression — what "the same step" means.</summary>
    private static string stepKey(StepMethodInfo step)
        => (step.StepKind == "Check" ? "Then" : step.StepKind) + "|" + step.Expression;

    private static StepMethodInfo? extractStepMethod(IMethodSymbol method)
    {
        string? expression = null;
        string? kind = null;

        // [Check] wins over [Then] when both sit on one method, in either order. Editor tooling
        // (the VS Code Cucumber extension, Rider's Reqnroll plugin) only recognises the
        // Given/When/Then short names, so stacking a [Then] with the same expression beside a
        // [Check] is the documented way to make a check navigable — see docs/editor-integration.md.
        // Without this rule the outcome would depend on attribute order, and a [Check]
        // silently downgraded to a [Then] discards the bool instead of asserting on it.
        foreach (var attr in method.GetAttributes())
        {
            var attrName = attr.AttributeClass?.Name;
            if (attrName == "GivenAttribute") { kind = "Given"; expression = attr.ConstructorArguments[0].Value?.ToString(); }
            else if (attrName == "WhenAttribute") { kind = "When"; expression = attr.ConstructorArguments[0].Value?.ToString(); }
            else if (attrName == "ThenAttribute" && kind != "Check") { kind = "Then"; expression = attr.ConstructorArguments[0].Value?.ToString(); }
            else if (attrName == "CheckAttribute") { kind = "Check"; expression = attr.ConstructorArguments[0].Value?.ToString(); }
        }

        if (expression == null || kind == null) return null;

        var (returnType, qualifiedReturnType, isAwaitable) = unwrapReturnType(method.ReturnType);

        var info = new StepMethodInfo
        {
            MethodName = method.Name,
            Expression = expression,
            StepKind = kind,
            IsAsync = isAwaitable,
            ReturnType = returnType,
            QualifiedReturnType = qualifiedReturnType,
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
                    info.ScopeResourceName ??= namedString(attr, "Resource");
                    break;
                case "ScopePerRowAttribute":
                    info.ScopePerRow = true;
                    info.ScopeResourceName ??= namedString(attr, "Resource");
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
    /// Discover a lifecycle hook: an attribute wins, otherwise the method name decides.
    /// Wolverine-style — convention first, attribute only as the override.
    /// </summary>
    private static HookMethodInfo? extractHook(IMethodSymbol method)
    {
        HookKind? kind = null;

        foreach (var attr in method.GetAttributes())
        {
            switch (attr.AttributeClass?.Name)
            {
                case "BeforeEachAttribute": kind = HookKind.BeforeEach; break;
                case "AfterEachAttribute": kind = HookKind.AfterEach; break;
                case "BeforeAllAttribute": kind = HookKind.BeforeAll; break;
                case "AfterAllAttribute": kind = HookKind.AfterAll; break;
            }
        }

        if (kind == null)
        {
            switch (stripAsync(method.Name))
            {
                case "BeforeEach": kind = HookKind.BeforeEach; break;
                case "AfterEach": kind = HookKind.AfterEach; break;
                case "BeforeAll": kind = HookKind.BeforeAll; break;
                case "AfterAll": kind = HookKind.AfterAll; break;
                default: return null;
            }
        }

        var (_, _, isAwaitable) = unwrapReturnType(method.ReturnType);

        var hook = new HookMethodInfo
        {
            MethodName = method.Name,
            Kind = kind.Value,
            IsAsync = isAwaitable,
            IsStatic = method.IsStatic,
        };

        foreach (var param in method.Parameters)
        {
            hook.Parameters.Add(ExtractParameter(param));
        }

        return hook;
    }

    private static string stripAsync(string name)
        => name.EndsWith("Async", StringComparison.Ordinal) && name.Length > 5
            ? name.Substring(0, name.Length - 5)
            : name;

    /// <summary>
    /// Feature-level hooks run before any scenario scope exists, so a scoped-service ask
    /// there is a compile error rather than a runtime surprise. Also catches hooks that
    /// take a value the Gherkin can't supply, and BeforeAll/AfterAll declared non-static.
    /// </summary>
    private static bool validateHooks(FixtureInfo fixture, SourceProductionContext spc)
    {
        var ok = true;

        foreach (var hook in fixture.Hooks)
        {
            if (hook.IsFeatureLevel && !hook.IsStatic)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.HookMustBeStatic, Microsoft.CodeAnalysis.Location.None,
                    hook.MethodName, fixture.ClassName));
                ok = false;
            }

            if (!hook.IsFeatureLevel && hook.IsStatic)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.HookMustBeInstance, Microsoft.CodeAnalysis.Location.None,
                    hook.MethodName, fixture.ClassName));
                ok = false;
            }

            foreach (var p in hook.Parameters)
            {
                var isScoped = p.Binding == ParameterBinding.ScopedService
                               || p.Binding == ParameterBinding.KeyedService;

                if (hook.IsFeatureLevel && isScoped)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.ScopedServiceInFeatureHook, Microsoft.CodeAnalysis.Location.None,
                        p.Name, p.Type, hook.MethodName));
                    ok = false;
                }
                else if (p.Binding == ParameterBinding.Value)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.UninjectableHookParameter, Microsoft.CodeAnalysis.Location.None,
                        p.Name, p.Type, hook.MethodName));
                    ok = false;
                }
            }
        }

        return ok;
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
            QualifiedType = qualified(param.Type),
            IsOut = param.RefKind == RefKind.Out,
            IsSimpleType = IsSimpleType(param.Type),
        };

        foreach (var attr in param.GetAttributes())
        {
            switch (attr.AttributeClass?.Name)
            {
                case "FromScopedServiceAttribute":
                    info.Binding = ParameterBinding.ScopedService;
                    info.ResourceName = positionalOrNamedString(attr, "Resource");
                    break;
                case "FromRootServiceAttribute":
                    info.Binding = ParameterBinding.RootService;
                    info.ResourceName = positionalOrNamedString(attr, "Resource");
                    break;
                case "FromKeyedServicesAttribute":
                    info.Binding = ParameterBinding.KeyedService;
                    info.ServiceKey = attr.ConstructorArguments.Length > 0
                        ? attr.ConstructorArguments[0].Value?.ToString()
                        : null;
                    info.ResourceName = namedString(attr, "Resource");
                    break;
            }
        }

        if (info.Binding != ParameterBinding.Value)
        {
            info.IsExplicitlyInjected = true;
            return info;
        }

        if (param.Type.ToDisplayString().TrimEnd('?') == "Bobcat.StepTable")
        {
            // The whole trailing data table as one argument. Explicit so it never claims a
            // same-named column and never falls through to DI.
            info.Binding = ParameterBinding.Table;
            info.IsExplicitlyInjected = true;
            info.IsSimpleType = false;
        }
        else if (param.Type.ToDisplayString() == "Bobcat.Engine.IStepContext")
        {
            info.Binding = ParameterBinding.StepContext;
            info.IsExplicitlyInjected = true;
        }
        else if (implementsInterface(param.Type, "Bobcat.Runtime.ITestResource"))
        {
            info.Binding = ParameterBinding.Resource;
            info.IsExplicitlyInjected = true;
        }
        else if (!info.IsSimpleType && param.RefKind != RefKind.Out)
        {
            // A type no Gherkin cell can produce — resolve it from the scenario scope.
            info.Binding = ParameterBinding.ScopedService;
        }

        return info;
    }

    private static bool implementsInterface(ITypeSymbol type, string interfaceName)
    {
        if (type.ToDisplayString() == interfaceName) return true;

        foreach (var iface in type.AllInterfaces)
        {
            if (iface.ToDisplayString() == interfaceName) return true;
        }

        return false;
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

            // A type name in the step text ({type}/{aggregate}/{command}/{event}/{readmodel}/
            // {message}), resolved against the compilation and emitted as typeof(global::...).
            // Captures only — a data-table cell cannot supply one.
            case CucumberExpressionParser.TypeCSharpType:
                return true;
        }

        return false;
    }

    private static string? positionalOrNamedString(AttributeData attr, string namedKey)
        => (attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value?.ToString() : null)
           ?? namedString(attr, namedKey);

    private static string? namedString(AttributeData attr, string key)
        => attr.NamedArguments.FirstOrDefault(a => a.Key == key).Value.Value?.ToString();

    private static FixtureInfo? findFixture(FeatureInfo feature, ImmutableArray<FixtureInfo> fixtures)
    {
        return fixtures.FirstOrDefault(f =>
            string.Equals(f.Title, feature.Title, StringComparison.OrdinalIgnoreCase));
    }

    private static List<MatchedScenario>? matchScenarios(FeatureInfo feature, FixtureInfo fixture,
        ImmutableArray<TableGrammarInfo> grammars, TypeNameResolver resolver, SourceProductionContext spc)
    {
        var matched = new List<MatchedScenario>();
        var hasErrors = false;

        foreach (var scenario in feature.Scenarios)
        {
            var matchedScenario = new MatchedScenario { Scenario = scenario };

            foreach (var step in scenario.Steps)
            {
                StepMatcher.MatchResult? match;
                try
                {
                    match = StepMatcher.Match(step, fixture);
                }
                catch (InvalidOperationException ex)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.AmbiguousStep, Microsoft.CodeAnalysis.Location.None,
                        step.Text, fixture.ClassName, ex.Message));
                    hasErrors = true;
                    continue;
                }

                if (match != null)
                {
                    if (!resolveTypeCaptures(step, match, resolver, spc)) hasErrors = true;

                    if (match.Method.IsSetVerification && (step.TableRows == null || step.TableHeaders == null))
                    {
                        // Without this the step would call the method, discard the collection and
                        // pass — a verification that verifies nothing.
                        spc.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.SetVerificationNeedsTable, Microsoft.CodeAnalysis.Location.None,
                            step.Text, match.Method.MethodName));
                        hasErrors = true;
                    }

                    matchedScenario.Steps.Add(new MatchedStep { Step = step, Match = match });
                    continue;
                }

                // A table grammar declares its own step text and is matched independently of
                // the fixture, so a shared grammar can be dropped into any feature.
                var grammarMatch = StepMatcher.MatchTableGrammar(step, grammars);
                if (grammarMatch != null)
                {
                    if (step.TableRows == null || step.TableHeaders == null)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.TableGrammarNeedsTable, Microsoft.CodeAnalysis.Location.None,
                            step.Text, grammarMatch.Grammar.ClassName));
                        hasErrors = true;
                        continue;
                    }

                    var grammar = grammarMatch.Grammar;

                    // A recipe supplies the per-row sink, so Row becomes optional — but only
                    // when the recipe names the entity type it should construct.
                    if (grammar.Row == null && !(grammar.HasRecipe && grammar.RecipeEntity != null))
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.TableGrammarNeedsRow, Microsoft.CodeAnalysis.Location.None,
                            grammar.ClassName));
                        hasErrors = true;
                        continue;
                    }

                    if (grammar.HasRecipe && grammar.Row is { HasReturnValue: false })
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.RecipeRowMustReturnProduct, Microsoft.CodeAnalysis.Location.None,
                            grammar.ClassName));
                        hasErrors = true;
                        continue;
                    }

                    matchedScenario.Steps.Add(new MatchedStep { Step = step, GrammarMatch = grammarMatch });
                    continue;
                }

                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.UnmatchedStep,
                    Microsoft.CodeAnalysis.Location.None,
                    step.Text, fixture.ClassName));
                hasErrors = true;
            }

            matched.Add(matchedScenario);
        }

        return hasErrors ? null : matched;
    }

    /// <summary>
    /// Replace every type-name capture (<c>{aggregate}</c>, <c>{command}</c>, … — anything whose
    /// C# type is <c>System.Type</c>) in a match with the <c>global::</c>-qualified name of the type
    /// it names in the consuming compilation, so the emitter can write <c>typeof(...)</c>. The rule:
    /// a dotted name must match a type's full name; a simple name must match exactly one type, by
    /// simple name, across the compilation and every non-framework assembly it references. No match
    /// is BOBCAT011; more than one is BOBCAT012 (qualify the name in the step text).
    /// </summary>
    private static bool resolveTypeCaptures(StepInfo step, StepMatcher.MatchResult match,
        TypeNameResolver resolver, SourceProductionContext spc)
    {
        var parsed = match.Method.ParsedExpression;
        if (parsed == null) return true;

        var ok = true;
        for (var i = 0; i < parsed.Parameters.Count && i < match.ExtractedValues.Count; i++)
        {
            if (parsed.Parameters[i].CSharpType != CucumberExpressionParser.TypeCSharpType) continue;

            var name = match.ExtractedValues[i];
            var resolution = resolver.Resolve(name);

            if (resolution.Qualified != null)
            {
                match.ExtractedValues[i] = resolution.Qualified;
            }
            else if (resolution.Candidates.Count > 1)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.AmbiguousTypeName, Microsoft.CodeAnalysis.Location.None,
                    name, step.Text, string.Join(", ", resolution.Candidates)));
                ok = false;
            }
            else
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.UnresolvedTypeName, Microsoft.CodeAnalysis.Location.None,
                    name, step.Text));
                ok = false;
            }
        }

        return ok;
    }

    /// <summary>
    /// Build the compile-time model for a <c>[TableGrammar]</c> class, discovering its
    /// <c>Before</c>/<c>Row</c>/<c>After</c> methods by convention (attributes override).
    /// </summary>
    private static TableGrammarInfo? extractTableGrammar(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol symbol) return null;
        if (symbol.IsAbstract) return null;

        var info = new TableGrammarInfo
        {
            ClassName = symbol.Name,
            FullyQualifiedName = qualified(symbol),
        };

        var isTableGrammar = false;
        foreach (var attr in symbol.GetAttributes())
        {
            switch (attr.AttributeClass?.Name)
            {
                case "TableGrammarAttribute":
                    isTableGrammar = true;
                    info.Expression = attr.ConstructorArguments.Length > 0
                        ? attr.ConstructorArguments[0].Value?.ToString() ?? ""
                        : "";
                    break;
                case "ScopePerRowAttribute":
                    info.ScopePerRow = true;
                    info.ScopeResourceName = namedString(attr, "Resource");
                    break;
                case "ApproxAttribute":
                    if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is double tol)
                        info.ApproxTolerance = tol;
                    break;
            }

            // A persistence recipe announces itself by deriving from GrammarBehaviorAttribute.
            // That is all the generator ever knows about it.
            if (attr.AttributeClass != null && derivesFrom(attr.AttributeClass, "Bobcat.Runtime.GrammarBehaviorAttribute"))
            {
                info.HasRecipe = true;
                info.RecipeEntity = extractRecipeEntity(attr);
            }
        }

        if (!isTableGrammar || info.Expression.Length == 0) return null;

        foreach (var member in symbol.GetMembers().OfType<IMethodSymbol>())
        {
            if (member.MethodKind != MethodKind.Ordinary) continue;

            var role = tableGrammarRole(member);
            if (role == null) continue;

            var (returnType, qualifiedReturnType, isAwaitable) = unwrapReturnType(member.ReturnType);
            var grammarMethod = new GrammarMethodInfo
            {
                MethodName = member.Name,
                IsAsync = isAwaitable,
                ReturnType = returnType,
                QualifiedReturnType = qualifiedReturnType,
            };

            foreach (var param in member.Parameters)
            {
                grammarMethod.Parameters.Add(ExtractParameter(param));
            }

            switch (role)
            {
                case "Before": info.Before = grammarMethod; break;
                case "After": info.After = grammarMethod; break;
                case "Row":
                    info.Row = grammarMethod;
                    foreach (var attr in member.GetAttributes())
                    {
                        if (attr.AttributeClass?.Name != "ExpectedAttribute") continue;
                        var colArg = attr.ConstructorArguments.Length > 0
                            ? attr.ConstructorArguments[0].Value?.ToString()
                            : null;
                        info.RowExpectedColumn = colArg ?? namedString(attr, "Column");
                    }
                    break;
            }
        }

        try
        {
            info.ParsedExpression = CucumberExpressionParser.Parse(info.Expression);
        }
        catch
        {
            // Reported as an unmatched step downstream.
        }

        return info;
    }

    /// <summary>
    /// The recipe's entity type: the attribute's single type argument (<c>[MartenEntities&lt;Customer&gt;]</c>)
    /// or a <c>typeof(...)</c> constructor argument. Absent means the entity comes from Row's return.
    /// </summary>
    private static EntityTypeInfo? extractRecipeEntity(AttributeData attr)
    {
        INamedTypeSymbol? entity = null;

        if (attr.AttributeClass is { IsGenericType: true } generic && generic.TypeArguments.Length == 1)
        {
            entity = generic.TypeArguments[0] as INamedTypeSymbol;
        }

        if (entity == null)
        {
            foreach (var arg in attr.ConstructorArguments)
            {
                if (arg.Value is INamedTypeSymbol named) { entity = named; break; }
            }
        }

        return entity == null ? null : describeEntityType(entity);
    }

    /// <summary>
    /// Capture the constructors and settable properties columns can bind to. Records land here
    /// as their primary constructor, which is why ctor binding comes first.
    /// </summary>
    private static EntityTypeInfo describeEntityType(INamedTypeSymbol entity)
    {
        var info = new EntityTypeInfo
        {
            FullyQualifiedName = qualified(entity),
            Name = entity.Name,
        };

        foreach (var ctor in entity.InstanceConstructors)
        {
            if (ctor.DeclaredAccessibility != Accessibility.Public) continue;
            if (ctor.IsStatic) continue;

            if (ctor.Parameters.Length == 0)
            {
                info.HasParameterlessConstructor = true;
                continue;
            }

            var parameters = new List<ParameterInfo>();
            foreach (var p in ctor.Parameters) parameters.Add(ExtractParameter(p));
            info.Constructors.Add(parameters);
        }

        foreach (var member in entity.GetMembers().OfType<IPropertySymbol>())
        {
            if (member.DeclaredAccessibility != Accessibility.Public) continue;
            if (member.SetMethod == null || member.SetMethod.DeclaredAccessibility != Accessibility.Public) continue;

            info.SettableProperties.Add(new ParameterInfo
            {
                Name = member.Name,
                Type = member.Type.ToDisplayString(),
                QualifiedType = qualified(member.Type),
                IsSimpleType = IsSimpleType(member.Type),
            });
        }

        return info;
    }

    private static bool derivesFrom(INamedTypeSymbol symbol, string baseTypeName)
    {
        var current = symbol.BaseType;
        while (current != null)
        {
            if (current.ToDisplayString() == baseTypeName) return true;
            current = current.BaseType;
        }
        return false;
    }

    private static string? tableGrammarRole(IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
        {
            switch (attr.AttributeClass?.Name)
            {
                case "BeforeAttribute": return "Before";
                case "RowAttribute": return "Row";
                case "AfterAttribute": return "After";
            }
        }

        switch (stripAsync(method.Name))
        {
            case "Before": return "Before";
            case "Row": return "Row";
            case "After": return "After";
            default: return null;
        }
    }

    /// <summary>
    /// Returns the effective return type (Task/ValueTask unwrapped to their argument, or
    /// "void" for void/Task/ValueTask) and whether the method is awaitable.
    /// </summary>
    private static (string ReturnType, string QualifiedReturnType, bool IsAwaitable) unwrapReturnType(ITypeSymbol returnType)
    {
        if (returnType.SpecialType == SpecialType.System_Void)
            return ("void", "void", false);

        if (returnType is INamedTypeSymbol named)
        {
            var name = named.Name;
            if (name == "Task" || name == "ValueTask")
            {
                if (named.TypeArguments.Length == 1)
                {
                    var arg = named.TypeArguments[0];
                    return (arg.ToDisplayString(), qualified(arg), true);
                }
                return ("void", "void", true); // non-generic Task/ValueTask
            }
        }

        return (returnType.ToDisplayString(), qualified(returnType), false);
    }

    /// <summary>
    /// The <c>global::</c>-qualified name of a type. Generated code lives in the fixture's own
    /// namespace, so an unqualified name can bind to a different type than the author meant.
    /// </summary>
    private static string qualified(ITypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static bool inheritsFrom(INamedTypeSymbol symbol, string baseTypeName)
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

    private static string deriveTitle(string name)
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

    public static readonly DiagnosticDescriptor ScopedServiceInFeatureHook = new(
        "BOBCAT004",
        "Scoped service in a feature-level hook",
        "Parameter '{0}' of type '{1}' cannot be injected into '{2}' — BeforeAll/AfterAll run once " +
        "per feature, before any scenario DI scope exists. Use [FromRootService], inject a test " +
        "resource, or move the work to BeforeEach/AfterEach.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor UninjectableHookParameter = new(
        "BOBCAT005",
        "Uninjectable lifecycle hook parameter",
        "Parameter '{0}' of type '{1}' on hook '{2}' cannot be resolved. Lifecycle hooks take " +
        "IStepContext, test resources, or services — not values from the Gherkin.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor HookMustBeStatic = new(
        "BOBCAT006",
        "Feature-level hook must be static",
        "'{0}' on fixture '{1}' must be static — BeforeAll/AfterAll run once per feature, while " +
        "fixture instances are created per scenario.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor TableGrammarNeedsTable = new(
        "BOBCAT008",
        "Table grammar step has no data table",
        "Step '{0}' matches the table grammar '{1}' but has no trailing data table. A table " +
        "grammar runs once-before, once per row, then once-after — it needs rows.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor TableGrammarNeedsRow = new(
        "BOBCAT009",
        "Table grammar has no Row method",
        "Table grammar '{0}' has no per-row method. Add a method named 'Row' (or 'RowAsync'), " +
        "mark one with [Row], or name the entity type on the recipe (e.g. [MartenEntities<Customer>]) " +
        "so columns can be bound by convention.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor RecipeRowMustReturnProduct = new(
        "BOBCAT010",
        "Recipe Row must return the entity to persist",
        "Table grammar '{0}' applies a persistence recipe, so its Row method must return the " +
        "entity to persist. Give Row a return type, or drop the recipe attribute.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor UnresolvedTypeName = new(
        "BOBCAT011",
        "Type name in step cannot be resolved",
        "'{0}' in step '{1}' names no type in this compilation or its references. A {{type}}/{{aggregate}}/" +
        "{{command}}/{{event}}/{{readmodel}}/{{message}} capture must be a type's simple name (Account) or " +
        "its namespace-qualified name (Banking.Account). Is the project that declares it referenced?",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor AmbiguousTypeName = new(
        "BOBCAT012",
        "Type name in step is ambiguous",
        "'{0}' in step '{1}' matches more than one type: {2}. Qualify it with its namespace in the step text.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor AmbiguousStep = new(
        "BOBCAT013",
        "Ambiguous step",
        "Step '{0}' matches more than one method on fixture '{1}' (or its base classes / grammar modules): {2}",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor SetVerificationNeedsTable = new(
        "BOBCAT014",
        "Set verification step has no data table",
        "Step '{0}' matches the [SetVerification] method '{1}' but has no trailing data table, so there is " +
        "nothing to compare the returned collection against. Add the expected rows, or match a different step.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor StepHidesBaseStep = new(
        "BOBCAT015",
        "Step hides the same step on a base class",
        "Step '{0}' on '{1}' hides the same step declared on base class '{2}'; the most-derived declaration is bound.",
        "Bobcat",
        DiagnosticSeverity.Info,
        true);

    public static readonly DiagnosticDescriptor HookMustBeInstance = new(
        "BOBCAT007",
        "Scenario-level hook must be an instance method",
        "'{0}' on fixture '{1}' must be an instance method — BeforeEach/AfterEach run against the " +
        "scenario's fixture instance.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);
}
