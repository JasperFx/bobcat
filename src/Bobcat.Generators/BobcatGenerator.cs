using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

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

        // 3b. Collect code-first specifications (issue #170): [Scenario] methods on Specification
        //     subclasses build runtime FeatureDefinitions the Gherkin pipeline never sees, so
        //     their Event Modeling slice declarations are read here, Roslyn-side, keeping the
        //     {Feature}/{Scenario} identity stamping identical for both authoring styles.
        var specifications = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => node is ClassDeclarationSyntax cds && cds.BaseList != null,
                transform: (ctx, ct) => CodeFirstSpecs.Extract(ctx, ct))
            .Where(s => s != null)
            .Select((s, _) => s!);

        // 3c. Collect calls to [BobcatStep] helpers (issue #110). Each one becomes an interceptor
        //     that reports the step as it runs — the only way a step declared OUTSIDE the test
        //     body, on a shared helper, can report progress without the test being edited.
        var stepCalls = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => node is InvocationExpressionSyntax,
                transform: StepInterceptors.Extract)
            .Where(c => c != null)
            .Select((c, _) => c!);

        context.RegisterSourceOutput(stepCalls.Collect(), (spc, calls) =>
        {
            if (calls.Length == 0) return;
            spc.AddSource("BobcatStepInterceptors.g.cs", StepInterceptors.Emit(calls));
        });

        // 3d. Collect [BobcatFeature] classes whose test bodies declare their steps as marker
        //     comments (issue #110). Comments are erased by the compiler, so unlike every other
        //     authoring style this one cannot be recorded as it executes — the syntax tree is the
        //     only place the information exists, and a module initializer carries it to runtime.
        var markedSpecs = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => node is ClassDeclarationSyntax cds && cds.AttributeLists.Count > 0,
                transform: MarkerCommentSpecs.Extract)
            .Where(s => s != null)
            .Select((s, _) => s!);

        context.RegisterSourceOutput(markedSpecs.Collect(), (spc, specs) =>
        {
            if (!MarkerCommentSpecs.HasSteps(specs)) return;
            spc.AddSource("BobcatDeclaredSteps.g.cs", MarkerCommentSpecs.Emit(specs));
        });

        // 4. Combine features + fixtures + table grammars + code-first specs + the compilation
        //    (type-name captures such as {aggregate} are resolved against it — see
        //    resolveTypeCaptures).
        var combined = featureFiles.Collect()
            .Combine(fixtureClasses.Collect())
            .Combine(tableGrammars.Collect())
            .Combine(specifications.Collect())
            .Combine(context.CompilationProvider);

        // 5. Generate source
        context.RegisterSourceOutput(combined, (spc, pair) =>
        {
            var features = pair.Left.Left.Left.Left;
            var fixtures = pair.Left.Left.Left.Right;
            var grammars = pair.Left.Left.Right;
            var specs = pair.Left.Right;
            var resolver = new TypeNameResolver(pair.Right);

            // Issue #106. Only emit Event Modeling descriptors where the consuming compilation can
            // actually host them. A type probe rather than an assembly-name check: JasperFx.Events
            // 2.53.0 shipped an early, incompatible sketch of this namespace, so "the assembly is
            // referenced" and "the shape emitted against exists" are different questions.
            var canEmitEventModel = pair.Right.GetTypeByMetadataName(EventModelEmitter.GateTypeName) != null;
            var slices = new Dictionary<string, EventModelEmitter.SliceModel>();

            foreach (var fixture in fixtures)
            {
                foreach (var hidden in fixture.HiddenSteps)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.StepHidesBaseStep, Microsoft.CodeAnalysis.Location.None,
                        hidden.Expression, hidden.DeclaringType, hidden.HiddenType));
                }

                // Issue #212: composition errors are reported per fixture, features or not —
                // a broken [IncludeGrammars] is a wiring mistake even before a feature binds it.
                foreach (var duplicate in fixture.DuplicateModules)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.DuplicateGrammarModule, Microsoft.CodeAnalysis.Location.None,
                        duplicate.DeclaringType, duplicate.Module));
                }

                foreach (var module in fixture.Modules)
                {
                    if (module.Problem == null) continue;
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.ModuleConstructionMismatch, Microsoft.CodeAnalysis.Location.None,
                        module.DisplayName, fixture.ClassName, module.Problem));
                }
            }

            foreach (var feature in features)
            {
                if (feature == null) continue;

                var fixture = findFixture(feature, fixtures);

                // The composition diagnostics above already failed the build; emitting code
                // against a broken module declaration would only bury them in C# errors.
                if (fixture is { HasCompositionErrors: true }) continue;

                if (fixture == null)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.NoMatchingFixture,
                        Microsoft.CodeAnalysis.Location.None,
                        feature.Title, conventionAdviceFor(feature.Title)));
                    continue;
                }

                // Issue #259: the parser has already inlined every arrangement reference, or
                // recorded why it could not. Either kind of failure suppresses the feature.
                if (!arrangementsAreSound(feature, spc)) continue;

                try
                {
                    if (!validateHooks(fixture, spc)) continue;

                    var matched = matchScenarios(feature, fixture, grammars, resolver, spc);
                    if (matched == null) continue;

                    var source = CodeEmitter.EmitFeature(feature, fixture, matched);
                    var fileName = CodeEmitter.SanitizeIdentifier(feature.Title) + "_Feature.g.cs";
                    spc.AddSource(fileName, source);

                    if (canEmitEventModel) EventModelEmitter.Collect(feature, matched, slices);
                }
                catch (Exception ex)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.GenerationError,
                        Microsoft.CodeAnalysis.Location.None,
                        feature.Title, ex.Message));
                }
            }

            // Issue #170: code-first specifications feed the same slice fold, so a team authoring
            // specs in C# gets the same event model — and the same Specifications bindings that
            // drive drift colouring — as the equivalent .feature files would produce.
            if (canEmitEventModel)
            {
                foreach (var spec in specs) EventModelEmitter.Collect(spec, slices);
            }

            // One IEventModelDefinitionSource per assembly, after every feature has contributed.
            // Emitted only when there is at least one slice: an empty source would register a
            // model with no slices and show up in a host's discovery as a blank canvas.
            if (canEmitEventModel && slices.Count > 0)
            {
                spc.AddSource(
                    "BobcatEventModelSource.g.cs",
                    EventModelEmitter.EmitSource(
                        pair.Right.AssemblyName ?? "Bobcat",
                        eventModelNameOf(pair.Right),
                        slices.Values.ToList()));
            }
        });

        // Issue #207: emit the Microsoft.Testing.Platform entry point for a spec assembly that
        // references Bobcat.Mtp and declares no Main of its own, so a consumer's setup is only
        // package references + .feature files. [BobcatConfiguration] methods are the configure
        // seam the generated Main calls. See EntryPointEmitter for the gates.
        var configurationMethods = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => node is MethodDeclarationSyntax mds && mds.AttributeLists.Count > 0,
                transform: (ctx, ct) => EntryPointEmitter.ExtractConfigurationMethod(ctx, ct))
            .Where(m => m != null)
            .Select((m, _) => m!);

        var entryPointInputs = context.CompilationProvider
            .Combine(configurationMethods.Collect())
            .Combine(context.AnalyzerConfigOptionsProvider);

        context.RegisterSourceOutput(entryPointInputs,
            (spc, pair) => emitEntryPoint(spc, pair.Left.Left, pair.Left.Right, pair.Right));
    }

    /// <summary>
    /// Decides whether this compilation gets a generated MTP entry point, and reports honestly
    /// on the [BobcatConfiguration] methods either way: a method the generated Main cannot call
    /// is BOBCAT016 (error — it was declared to be called), and a method that will never be
    /// called because no entry point is generated is BOBCAT017 (warning naming the reason),
    /// rather than being silently ignored.
    /// </summary>
    private static void emitEntryPoint(SourceProductionContext spc, Compilation compilation,
        ImmutableArray<EntryPointEmitter.ConfigurationMethodInfo> methods,
        AnalyzerConfigOptionsProvider config)
    {
        string? skipReason = null;

        if (compilation.GetTypeByMetadataName(EntryPointEmitter.GateTypeName) == null)
        {
            // Most Bobcat suites run in-process and never reference the MTP host package. No
            // entry point, and no warning either — unless a configuration method is waiting for
            // one, which is a wiring mistake worth naming.
            skipReason = "the compilation does not reference Bobcat.Mtp";
        }
        else if (config.GlobalOptions.TryGetValue(EntryPointEmitter.GenerateEntryPointProperty, out var configured)
                 && string.Equals(configured, "false", StringComparison.OrdinalIgnoreCase))
        {
            skipReason = "the BobcatGenerateEntryPoint MSBuild property is false";
        }
        else if (compilation.Options.OutputKind != OutputKind.ConsoleApplication)
        {
            skipReason = "the project does not build an executable (set <OutputType>Exe</OutputType>)";
        }
        else if (compilation.GetEntryPoint(spc.CancellationToken) != null)
        {
            // A hand-written Main is authoritative — this is what makes CS0017 impossible and
            // keeps every existing consumer compiling unchanged.
            skipReason = "the assembly declares its own entry point, which owns configuration";
        }

        if (skipReason != null)
        {
            foreach (var method in methods)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.ConfigurationMethodNotCalled, Microsoft.CodeAnalysis.Location.None,
                    method.DisplayName, skipReason));
            }

            return;
        }

        var callable = new List<EntryPointEmitter.ConfigurationMethodInfo>();
        foreach (var method in methods)
        {
            if (method.Problem != null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.InvalidConfigurationMethod, Microsoft.CodeAnalysis.Location.None,
                    method.DisplayName, method.Problem));
                continue;
            }

            callable.Add(method);
        }

        spc.AddSource(EntryPointEmitter.HintName, EntryPointEmitter.EmitSource(callable));
    }

    private static FeatureInfo? parseFeatureFile(string path, string content)
    {
        return SimpleGherkinParser.Parse(content, path);
    }

    /// <summary>
    /// The model name declared by an assembly-level <c>[EventModelName("…")]</c>, or null for the
    /// assembly-name default. Matched by simple name like every other Bobcat attribute, because the
    /// netstandard2.0 generator references no runtime assembly. Upstream merges descriptors by model
    /// name, so this is how a spec assembly's slices land on the same model as the service's
    /// Wolverine-derived chains (issue #172).
    /// </summary>
    private static string? eventModelNameOf(Compilation compilation)
    {
        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            if (attribute.AttributeClass?.Name != "EventModelNameAttribute") continue;
            if (attribute.ConstructorArguments.Length == 1 &&
                attribute.ConstructorArguments[0].Value is string name &&
                name.Length > 0)
            {
                return name;
            }
        }

        return null;
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
            // The global namespace stringifies as the literal "<global namespace>", which is a
            // display string, not code. Emitting it produced `namespace <global namespace>;` and
            // 14 compile errors in a file the user cannot edit (issue #269). A fixture with no
            // namespace declaration is the shape a quickstart has, so this lands on first contact.
            Namespace = symbol.ContainingNamespace.IsGlobalNamespace
                ? ""
                : symbol.ContainingNamespace.ToDisplayString(),
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

        // Collect [IncludeGrammars] modules — declared on the fixture or inherited from a base
        // class (issue #212 phase 2), so a shipped assembly fixture can carry its modules.
        collectModules(symbol, info);

        return info;
    }

    /// <summary>
    /// Walk the fixture and its base classes for <c>[IncludeGrammars]</c>, most-derived first.
    /// <b>Most-derived wins</b> for a module type declared at several levels — that is how a
    /// derived fixture re-parameterizes a module its base declared. The same module type declared
    /// twice on <em>one</em> class is recorded for BOBCAT018: one instance per module type per
    /// fixture, because a second instance of the same vocabulary would make every one of its step
    /// texts ambiguous (the problem BOBCAT013 exists to close).
    /// </summary>
    private static void collectModules(INamedTypeSymbol symbol, FixtureInfo info)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var type = symbol; type != null && !isDiscoveryRoot(type); type = type.BaseType)
        {
            var declaredHere = new HashSet<string>(StringComparer.Ordinal);

            foreach (var attr in type.GetAttributes())
            {
                if (attr.AttributeClass?.Name != "IncludeGrammarsAttribute") continue;

                foreach (var (moduleSymbol, arguments) in readIncludeGrammars(attr))
                {
                    var fqn = qualified(moduleSymbol);

                    if (!declaredHere.Add(fqn))
                    {
                        info.DuplicateModules.Add(new DuplicateModuleInfo
                        {
                            Module = moduleSymbol.ToDisplayString(),
                            DeclaringType = type.ToDisplayString(),
                        });
                        continue;
                    }

                    // A more-derived class already declared this module type: its declaration
                    // (and its arguments) win, the way a derived step hides a base one.
                    if (!seen.Add(fqn)) continue;

                    info.Modules.Add(extractModuleInfo(moduleSymbol, arguments));
                }
            }
        }
    }

    /// <summary>
    /// The module types (with their construction arguments) one <c>[IncludeGrammars]</c> names.
    /// Two attribute shapes: <c>(params Type[])</c> — several modules, no arguments — and
    /// <c>(Type, params object?[])</c> — one module plus its constructor literals.
    /// </summary>
    private static IEnumerable<(INamedTypeSymbol Module, ImmutableArray<TypedConstant> Arguments)> readIncludeGrammars(
        AttributeData attr)
    {
        var args = attr.ConstructorArguments;
        if (args.Length == 0) yield break;

        if (args[0].Kind == TypedConstantKind.Type)
        {
            // (Type module, params object?[] arguments)
            if (args[0].Value is INamedTypeSymbol module)
            {
                var arguments = args.Length > 1 && args[1].Kind == TypedConstantKind.Array
                    ? args[1].Values
                    : ImmutableArray<TypedConstant>.Empty;
                yield return (module, arguments);
            }

            yield break;
        }

        // (params Type[] modules)
        foreach (var constant in args[0].Values)
        {
            if (constant.Value is INamedTypeSymbol module)
                yield return (module, ImmutableArray<TypedConstant>.Empty);
        }
    }

    private static ModuleInfo extractModuleInfo(INamedTypeSymbol moduleSymbol, ImmutableArray<TypedConstant> arguments)
    {
        var module = new ModuleInfo
        {
            FullyQualifiedName = qualified(moduleSymbol),
            DisplayName = moduleSymbol.ToDisplayString(),
            IsFixture = inheritsFrom(moduleSymbol, "Bobcat.Fixture"),
        };

        // A module's steps are inherited the same way a fixture's are, so a shipped grammar base
        // class composes in through [IncludeGrammars] via an empty subclass.
        collectStepsAndHooks(moduleSymbol, module.StepMethods, hooks: null, hidden: null);
        foreach (var stepMethod in module.StepMethods)
            stepMethod.DeclaringModule = module.FullyQualifiedName;

        bindModuleConstruction(moduleSymbol, arguments, module);

        return module;
    }

    /// <summary>
    /// Decide how the generated code constructs the module (issue #212 phase 2). The attribute's
    /// literals bind positionally to the constructor's value parameters, in declaration order;
    /// parameters no literal covers are resolved like step parameters (IStepContext, test
    /// resources, scoped services — the <see cref="ExtractParameter"/> rules, so
    /// <c>[FromRootService]</c> and friends work on a module constructor too); trailing optional
    /// value parameters may be omitted. A shape nothing satisfies is recorded on
    /// <see cref="ModuleInfo.Problem"/> and reported as BOBCAT019.
    /// </summary>
    private static void bindModuleConstruction(INamedTypeSymbol moduleSymbol,
        ImmutableArray<TypedConstant> arguments, ModuleInfo module)
    {
        var constructors = moduleSymbol.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .OrderByDescending(c => c.Parameters.Length)
            .ToList();

        if (constructors.Count == 0)
        {
            module.Problem = $"'{module.DisplayName}' has no public constructor";
            return;
        }

        string? firstFailure = null;

        foreach (var constructor in constructors)
        {
            var attempt = tryBindConstructor(constructor, arguments);
            if (attempt.Problem == null)
            {
                module.ConstructionParameters.AddRange(attempt.Bound);
                return;
            }

            firstFailure ??= attempt.Problem;
        }

        module.Problem = firstFailure;
    }

    private static (List<ModuleConstructionArg> Bound, string? Problem) tryBindConstructor(
        IMethodSymbol constructor, ImmutableArray<TypedConstant> arguments)
    {
        var bound = new List<ModuleConstructionArg>();
        var next = 0;

        foreach (var parameter in constructor.Parameters)
        {
            var info = ExtractParameter(parameter);

            if (info.Binding == ParameterBinding.Value && next < arguments.Length)
            {
                var literal = literalOf(arguments[next]);
                if (literal == null)
                {
                    return (bound, $"argument {next + 1} cannot be written as a literal for " +
                                   $"parameter '{parameter.Name}' ({info.Type})");
                }

                bound.Add(new ModuleConstructionArg { Parameter = info, Literal = literal });
                next++;
            }
            else if (info.Binding != ParameterBinding.Value && info.Binding != ParameterBinding.Table)
            {
                // Resolved from the scenario at construction time — same rules as a step parameter.
                bound.Add(new ModuleConstructionArg { Parameter = info, Literal = null });
            }
            else if (parameter.HasExplicitDefaultValue)
            {
                // Omitted; named-argument emission makes the gap legal.
            }
            else
            {
                return (bound, $"parameter '{parameter.Name}' ({info.Type}) is covered by no " +
                               "attribute argument, is not resolvable from the scenario scope, and has no default");
            }
        }

        return next < arguments.Length
            ? (bound, $"{arguments.Length} argument(s) were given but only {next} bind to constructor parameters")
            : (bound, null);
    }

    /// <summary>
    /// A <c>[IncludeGrammars]</c> attribute argument as the C# literal the generated construction
    /// writes — string/char escaped, numeric suffixed, enums cast, <c>typeof(global::…)</c> for a
    /// type. Null when the constant has no writable form (an array, an unexpected shape).
    /// </summary>
    private static string? literalOf(TypedConstant constant)
    {
        if (constant.IsNull) return "null";

        switch (constant.Kind)
        {
            case TypedConstantKind.Type:
                return constant.Value is ITypeSymbol type ? $"typeof({qualified(type)})" : null;

            case TypedConstantKind.Enum:
                return constant.Type is INamedTypeSymbol enumType
                    ? $"({qualified(enumType)})({formatPrimitive(constant.Value)})"
                    : null;

            case TypedConstantKind.Primitive:
                return formatPrimitive(constant.Value);

            default:
                return null;
        }
    }

    private static string? formatPrimitive(object? value)
        => value switch
        {
            null => "null",
            string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"",
            char c => "'" + (c == '\'' ? "\\'" : c == '\\' ? "\\\\" : c.ToString()) + "'",
            bool b => b ? "true" : "false",
            float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f",
            double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d",
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture) + "L",
            ulong ul => ul.ToString(System.Globalization.CultureInfo.InvariantCulture) + "UL",
            uint ui => ui.ToString(System.Globalization.CultureInfo.InvariantCulture) + "u",
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };

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

            // `StepTable` says the step needs one; `StepTable?` says it may. Enforced as
            // BOBCAT020 rather than left to become a null-deref inside the fixture (#233).
            info.TableRequired = param.NullableAnnotation == NullableAnnotation.NotAnnotated;
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

            // A type name in the step text ({type}/{aggregate}/{command}/{event}/{readmodel}/{document}/
            // {saga}/{message}), resolved against the compilation and emitted as typeof(global::...).
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

                    if (step.TableRows == null &&
                        match.Method.Parameters.Any(p => p.Binding == ParameterBinding.Table && p.TableRequired))
                    {
                        // Without this the step calls the method with null and the fixture
                        // dereferences it — an NRE whose stack is Bobcat's, not the author's.
                        spc.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.StepNeedsTable, Microsoft.CodeAnalysis.Location.None,
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

                // Issue #259: an arrangement is referenced as `the arrangement "…"`, so a Given that
                // writes an arrangement's NAME bare is a reference in the wrong shape. Saying how to
                // write it beats a generic "unmatched step".
                var nearest = Arrangements.NearestName(feature, step.Text);
                if (nearest != null)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.UnknownArrangement, Microsoft.CodeAnalysis.Location.None,
                        feature.Title,
                        $"the step '{step.Text}' matches no step on fixture '{fixture.ClassName}', but looks like the " +
                        $"arrangement \"{nearest}\" — an arrangement is referenced as 'the arrangement \"{nearest}\"'"));
                    hasErrors = true;
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
    /// Reports why the feature's <c>@arrangement</c> scenarios could not be expanded: BOBCAT022 for
    /// a broken arrangement or reference, BOBCAT021 for a <c>the arrangement "…"</c> naming none the
    /// feature declares. A false return suppresses the feature, so the diagnostic is not buried
    /// under the errors an unexpanded reference would otherwise produce.
    /// </summary>
    private static bool arrangementsAreSound(FeatureInfo feature, SourceProductionContext spc)
    {
        foreach (var problem in feature.ArrangementProblems)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.InvalidArrangement, Microsoft.CodeAnalysis.Location.None, feature.Title, problem));
        }

        foreach (var unknown in feature.UnknownArrangementReferences)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.UnknownArrangement, Microsoft.CodeAnalysis.Location.None, feature.Title,
                Arrangements.Describe(feature, unknown)));
        }

        return feature.ArrangementProblems.Count == 0 && feature.UnknownArrangementReferences.Count == 0;
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

    /// <summary>
    /// What to actually tell someone whose feature bound to nothing. Issue #273: the old message
    /// pasted "Fixture" onto the feature title, and following it did not clear the diagnostic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Convention matching runs the other way — class name, minus a trailing "Fixture", split on
    /// camel humps by <see cref="deriveTitle"/> — so title and class name are inverses only when
    /// the title's spaces already sit exactly where the humps are. Two common titles where they
    /// do not:
    /// </para>
    /// <list type="bullet">
    /// <item><c>BookingShipments</c> was told to write <c>BookingShipmentsFixture</c>, which
    /// derives back to "Booking Shipments" and matches nothing. That is the case in the report:
    /// following the diagnostic's own advice left the diagnostic in place, and only
    /// <c>[FixtureTitle]</c> ever worked.</item>
    /// <item><c>Wallet over HTTP</c> was told to write <c>Wallet over HTTPFixture</c>, which is
    /// not a legal identifier at all.</item>
    /// </list>
    /// <para>
    /// So the conventional name is offered only when it is real: build the candidate, run it back
    /// through the same derivation the matcher uses, and suggest it only if the round trip lands
    /// on this title. When nothing does, say so — an attribute-only instruction that works beats a
    /// convention that does not.
    /// </para>
    /// </remarks>
    private static string conventionAdviceFor(string title)
    {
        var candidate = new string(title.Where(c => !char.IsWhiteSpace(c)).ToArray()) + "Fixture";

        var legal = candidate.Length > "Fixture".Length
                    && (char.IsLetter(candidate[0]) || candidate[0] == '_')
                    && candidate.All(c => char.IsLetterOrDigit(c) || c == '_');

        var roundTrips = legal && string.Equals(
            deriveTitle(candidate.Substring(0, candidate.Length - "Fixture".Length)),
            title,
            StringComparison.OrdinalIgnoreCase);

        return roundTrips
            ? $"Create a fixture class with [FixtureTitle(\"{title}\")] or name it {candidate}."
            : $"Create a fixture class with [FixtureTitle(\"{title}\")]. No class name derives to " +
              "this title by convention (the convention splits a class name on its camel humps), " +
              "so the attribute is the only way to bind it.";
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
    /// <summary>
    /// A <c>.feature</c> in <c>AdditionalFiles</c> that binds to no fixture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An error since issue #273, and the odd one out until then.</b> A feature file is a
    /// declaration of intent; no fixture for it is a mistake, not a preference. The scenarios
    /// exist, someone believes they are covered, and nothing runs — a spec that cannot fail,
    /// which is the one thing this stack exists to prevent. As a warning it scrolled past in a
    /// normal build and was invisible in CI, and the run it left behind reported
    /// "Zero tests ran … total: 0" with exit code 0.
    /// </para>
    /// <para>
    /// It also matches how the closed step vocabulary already behaves: an unmatched <em>step</em>
    /// is BOBCAT002, a build error. An unmatched <em>feature</em> being a warning was the
    /// inconsistency.
    /// </para>
    /// <para>
    /// The advice is composed per feature ({1}) rather than templated, because the templated form
    /// was wrong — see <c>conventionAdviceFor</c>.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor NoMatchingFixture = new(
        "BOBCAT001",
        "No matching fixture",
        "No fixture found for feature '{0}'. {1}",
        "Bobcat",
        DiagnosticSeverity.Error,
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
        "{{command}}/{{event}}/{{readmodel}}/{{message}}/{{document}}/{{saga}} capture must be a type's simple name (Account) or " +
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

    public static readonly DiagnosticDescriptor InvalidConfigurationMethod = new(
        "BOBCAT016",
        "Invalid [BobcatConfiguration] method",
        "The [BobcatConfiguration] method '{0}' cannot be called by the generated entry point: {1}. " +
        "It must be a static void method taking exactly one BobcatRunner parameter, reachable from " +
        "generated code.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor ConfigurationMethodNotCalled = new(
        "BOBCAT017",
        "[BobcatConfiguration] method is never called",
        "The [BobcatConfiguration] method '{0}' is never called, because no entry point is being " +
        "generated: {1}. Either remove the attribute or call the method from your own configuration.",
        "Bobcat",
        DiagnosticSeverity.Warning,
        true);

    public static readonly DiagnosticDescriptor DuplicateGrammarModule = new(
        "BOBCAT018",
        "Grammar module included more than once",
        "'{0}' includes the grammar module '{1}' more than once. One instance per module type per " +
        "fixture — a second instance of the same vocabulary would make every one of its step texts " +
        "ambiguous. To bind the same grammar against two targets, declare two thin module " +
        "subclasses with distinct step texts.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor ModuleConstructionMismatch = new(
        "BOBCAT019",
        "Grammar module cannot be constructed",
        "The grammar module '{0}' composed into fixture '{1}' cannot be constructed from its " +
        "[IncludeGrammars] declaration: {2}. Attribute arguments (constants and typeof only) bind " +
        "positionally to the constructor's value parameters; other parameters are resolved from " +
        "the scenario like step parameters (IStepContext, test resources, services); trailing " +
        "optional parameters may be omitted.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor StepNeedsTable = new(
        "BOBCAT020",
        "Step has no data table, and the method it binds requires one",
        "Step '{0}' has no trailing data table, but the method '{1}' it binds declares a non-nullable " +
        "Bobcat.StepTable parameter. Add the table, or declare the parameter as 'StepTable?' if the step " +
        "is meant to work without one.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor UnknownArrangement = new(
        "BOBCAT021",
        "Step names no arrangement",
        "Feature '{0}': {1}. An @arrangement scenario is referenced as 'Given the arrangement \"<name>\"' " +
        "(the name case-insensitive), from the same feature file.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor InvalidArrangement = new(
        "BOBCAT022",
        "Arrangement cannot be expanded",
        "Feature '{0}': {1}. An @arrangement scenario is a named list of Given steps, inlined wherever a " +
        "Given step says 'the arrangement \"<name>\"'; it never runs as a test of its own.",
        "Bobcat",
        DiagnosticSeverity.Error,
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
