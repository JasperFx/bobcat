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
        // 3c. Collect calls to [BobcatStep] helpers (issue #110). Each one becomes an interceptor
        //     that reports the step as it runs — the only way a step declared OUTSIDE the test
        //     body, on a shared helper, can report progress without the test being edited.
        var stepCalls = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => node is InvocationExpressionSyntax,
                transform: StepInterceptors.Extract)
            .Where(c => c != null)
            .Select((c, _) => c!);

        context.RegisterSourceOutput(
            stepCalls.Collect().Combine(context.AnalyzerConfigOptionsProvider).Combine(context.ParseOptionsProvider),
            (spc, input) =>
        {
            var ((extracted, config), parseOptions) = input;

            // Projected assertions are opt-in per PROJECT: extracted always (a transform cannot see
            // MSBuild properties), kept only when the project asked for them. Off, a suite behaves
            // exactly as it did — every Shouldly call throws where it always threw.
            var calls = ProjectedAssertions.Enabled(config)
                ? extracted
                : extracted.Where(c => !c.IsProjectedAssertion).ToImmutableArray();

            if (calls.Length == 0) return;

            // Reported once per (method, placeholder) rather than once per call site: the mistake
            // is in the attribute, and a helper called from forty tests would otherwise bury the
            // build in forty copies of one warning.
            var reported = new HashSet<string>();
            foreach (var call in calls)
            {
                foreach (var placeholder in call.UnknownPlaceholders)
                {
                    if (!reported.Add(call.DeclaringType + "." + call.MethodName + "/" + placeholder)) continue;

                    var parameters = call.ParameterNames.Count == 0
                        ? " (it takes none)"
                        : " (it takes " + string.Join(", ", call.ParameterNames) + ")";

                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.UnknownStepPlaceholder,
                        call.Location ?? Microsoft.CodeAnalysis.Location.None,
                        call.MethodName, placeholder, parameters));
                }
            }

            // bobcat#535. A step the interceptor cannot reach gets no interceptor, and one BOBCAT039 per
            // method at its declaration instead of a CS0122 per call site in a generated file.
            foreach (var call in calls.Where(c => c.Inaccessible != null))
            {
                if (!reported.Add(call.DeclaringType + "." + call.MethodName + "/inaccessible")) continue;

                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.InaccessibleStep,
                    call.Declaration ?? call.Location ?? Microsoft.CodeAnalysis.Location.None,
                    call.MethodName, call.Inaccessible));
            }

            calls = calls.Where(c => c.Inaccessible == null).ToImmutableArray();
            if (calls.Length == 0) return;

            // bobcat#484. Without the opt-in every interceptor below is a CS9137 in a generated file,
            // one per call site, and a project that gets the build to go green some other way (the
            // generator missing, or this error suppressed) has helpers that run as plain method calls:
            // no step opens, and a SpecAssert inside one has nothing to report to. One error, at the
            // first call site, that says which line to add, instead of a wall of CS9137s.
            if (!DeclaredValues.InterceptorsEnabled(parseOptions))
            {
                var first = calls
                    .Where(c => c.Location != null)
                    .OrderBy(c => c.Location!.SourceTree?.FilePath, StringComparer.Ordinal)
                    .ThenBy(c => c.Location!.SourceSpan.Start)
                    .FirstOrDefault();

                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.InterceptorsNotEnabled,
                    first?.Location ?? Microsoft.CodeAnalysis.Location.None,
                    calls.Length,
                    calls.Length == 1 ? "" : "s"));
                return;
            }

            // Numbered before either output: the interceptor writes each call's ordinal into the
            // recorder, and the plan is the list that ordinal indexes into. They have to agree.
            StepInterceptors.Number(calls);

            spc.AddSource("BobcatStepInterceptors.g.cs", StepInterceptors.Emit(calls));

            // The plan a projected test's grammar calls make up (preview, and the grey steps a
            // stopped scenario never reached). Only when some call site sits in a projected test —
            // a suite using [BobcatStep] helpers outside one gains no initializer.
            if (StepInterceptors.HasPlan(calls))
            {
                spc.AddSource("BobcatPlannedSteps.g.cs", StepInterceptors.EmitPlan(calls));
            }
        });

        // 3c''. `var theOrderId = Guid.NewGuid();` in a test names that value theOrderId in the spec
        var declaredValues = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => DeclaredValues.IsCandidate(node),
                transform: DeclaredValues.Extract)
            .Where(d => d != null)
            .Select((d, _) => d!);

        // Only where the project opted Bobcat.Generated into interceptors (buildTransitive does it for a
        // package reference). A plain analyzer reference has not, and an interceptor there is a hard
        // CS9137 — so the names quietly fall back to the learned ones instead.
        context.RegisterSourceOutput(declaredValues.Collect().Combine(context.ParseOptionsProvider), (spc, pair) =>
        {
            var (declarations, parseOptions) = pair;
            if (declarations.Length > 0 && DeclaredValues.InterceptorsEnabled(parseOptions))
                spc.AddSource("BobcatDeclaredValues.g.cs", DeclaredValues.Emit(declarations));
        });

        // 3c'. BOBCAT032 (bobcat#415, #420): a constant table, or a constant Specified<T>.With
        //      path, whose columns do not name members of the type it is about.
        var tableCalls = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => TableCallChecks.IsCandidate(node),
                transform: TableCallChecks.Extract)
            .Where(f => f != null)
            .Select((f, _) => f!);

        context.RegisterSourceOutput(tableCalls, (spc, findings) =>
        {
            foreach (var finding in findings)
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnknownTableColumn, finding.Location, finding.Message));
        });

        // 3f. BOBCAT034 (bobcat#462): an [EventModelRole] / [EventModelTrigger] naming no parameter of
        //     the vocabulary method it decorates, caught where the vocabulary is declared
        var roleSources = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => ProjectedRoles.IsDeclarationCandidate(node),
                transform: ProjectedRoles.CheckDeclaration)
            .Where(f => f != null)
            .Select((f, _) => f!);

        context.RegisterSourceOutput(roleSources, (spc, findings) =>
        {
            foreach (var finding in findings)
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnknownRoleSource, finding.Location, finding.Message));
        });

        // 3e. bobcat#449: each [BobcatFeature] test and the command or slice it exercises, as JasperFx
        //     specification bindings, so the specs join the Event Model by command type with no
        //     LinksToSpecification in the definition (jasperfx#995)
        var specBindings = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => SpecificationManifest.IsCandidate(node),
                transform: SpecificationManifest.Extract)
            .Where(f => f != null)
            .Select((f, _) => f!);

        // bobcat#462: ResolvedTypes rides the descriptor only where this JasperFx has the property
        var bindingContractVisible = context.CompilationProvider.Select((compilation, _) =>
        {
            var descriptor = compilation.GetTypeByMetadataName(SpecificationManifest.GateTypeName);
            var visible = descriptor != null
                          && compilation.GetTypeByMetadataName(SpecificationManifest.ManifestAttributeName) != null;
            return (Visible: visible, CarriesResolvedTypes: visible && descriptor!.GetMembers("ResolvedTypes").Length > 0);
        });

        context.RegisterSourceOutput(specBindings.Collect().Combine(bindingContractVisible), (spc, pair) =>
        {
            var (features, contract) = pair;
            if (!contract.Visible || features.Length == 0) return;
            spc.AddSource("BobcatSpecificationBindings.g.cs", SpecificationManifest.Emit(features, contract.CarriesResolvedTypes));
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

        // 3e. The specifications a concrete class inherits from a shared test base (bobcat#600),
        //     possibly compiled into another assembly, each bound under the subclass's identity.
        var inheritedSpecs = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => InheritedSpecs.IsCandidate(node),
                transform: InheritedSpecs.Extract)
            .Where(s => s != null)
            .Select((s, _) => s!);

        var skipOverrides = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => node is MethodDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: InheritedSpecs.SkipOverride)
            .Where(x => x != null)
            .Select((x, _) => x!);

        context.RegisterSourceOutput(skipOverrides, (spc, where) =>
            spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.InheritedSpecOverriddenWithFact, where)));

        var allSpecs = markedSpecs.Collect()
            .Combine(inheritedSpecs.Collect())
            .Select((pair, _) => pair.Left.AddRange(pair.Right));

        context.RegisterSourceOutput(
            allSpecs.Combine(context.CompilationProvider),
            (spc, pair) =>
            {
                var (specs, compilation) = pair;
                if (!MarkerCommentSpecs.HasAnything(specs)) return;

                // The compilation is combined in only to name the test framework (issue #391):
                // a projected suite is filtered by ITS framework's spelling, so a listing has to
                // say which one owns the process rather than leaving a reader to assume.
                // bobcat#573: each identity's Event Model coordinates, folded by the same calls the
                // descriptor is, so `bobcat run --slice` and the platform traits agree with the model.
                // Pattern inference follows this JasperFx's contract where there is one, so a Query is
                // a Query only where the model can say so; with no JasperFx at all there is no model to
                // disagree with and the full vocabulary applies.
                var hostsModel = compilation.GetTypeByMetadataName(EventModelEmitter.GateTypeName) != null;
                var contract = hostsModel
                    ? new EventModelEmitter.Contract
                    {
                        Query = compilation.GetTypeByMetadataName("JasperFx.Events.EventModeling.SlicePattern")?.GetMembers("Query").Length > 0
                    }
                    : EventModelEmitter.Contract.Latest;

                spc.AddSource(
                    "BobcatDeclaredSteps.g.cs",
                    MarkerCommentSpecs.Emit(
                        specs,
                        MarkerCommentSpecs.FrameworkOf(compilation),
                        EventModelEmitter.Coordinates(specs, contract)));
            });

        // 4. Marker-comment specs feed the Event Model too (issue #324): they are emitted on their
        //    own branch above for the runtime narrative, but their [BobcatSlice] binding and the
        //    roles their bodies state (bobcat#462) have to reach the Event Model, which is
        //    assembled here against the compilation.
        var modelInputs = markedSpecs.Collect().Combine(inheritedSpecs.Collect()).Combine(context.CompilationProvider);

        context.RegisterSourceOutput(modelInputs, (spc, pair) =>
        {
            var ((marked, inherited), compilation) = pair;

            // Reported whether or not this compilation can host an Event Model descriptor: a
            // [BobcatSlice] that names two slices, or names one as a string where a type exists, is
            // wrong either way (issue #324).
            foreach (var spec in marked)
            {
                foreach (var problem in spec.Problems)
                {
                    var descriptor = problem.Id switch
                    {
                        "RecordsNothing" => Diagnostics.SpecRecordsNothing,
                        "ProseKeyword" => Diagnostics.ProseReadAsComment,
                        "UnresolvedRoute" => Diagnostics.UnresolvedTriggerRoute,
                        _ => problem.IsError ? Diagnostics.SliceBindingConflict : Diagnostics.PreferSliceType
                    };

                    spc.ReportDiagnostic(Diagnostic.Create(
                        descriptor,
                        problem.Where ?? Microsoft.CodeAnalysis.Location.None,
                        problem.Message));
                }
            }

            // bobcat#489. Two specifications under one identity are one specification to everything
            // keyed on it, so it is refused here, the one place that sees every test in the
            // compilation. Reported at EACH claimant, naming the others, so whichever file the author
            // has open says what it collides with.
            foreach (var duplicate in MarkerCommentSpecs.DuplicateIdentities(marked.Concat(inherited)))
            {
                foreach (var claim in duplicate.Claims)
                {
                    var others = duplicate.Claims.Where(x => !ReferenceEquals(x, claim)).ToList();

                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.DuplicateSpecIdentity,
                        claim.Where ?? Microsoft.CodeAnalysis.Location.None,
                        others.Select(x => x.Where).Where(x => x is not null).Cast<Microsoft.CodeAnalysis.Location>(),
                        duplicate.Identity,
                        string.Join(", ", others.Select(x => x.Describe()))));
                }
            }

            // Issue #106. Only emit Event Modeling descriptors where the consuming compilation can
            // actually host them. A type probe rather than an assembly-name check: JasperFx.Events
            // 2.53.0 shipped an early, incompatible sketch of this namespace, so "the assembly is
            // referenced" and "the shape emitted against exists" are different questions.
            if (compilation.GetTypeByMetadataName(EventModelEmitter.GateTypeName) == null) return;

            var slices = new Dictionary<string, EventModelEmitter.SliceModel>();
            foreach (var spec in marked) EventModelEmitter.Collect(spec, slices);

            // One IEventModelDefinitionSource per assembly, after every spec has contributed.
            // Emitted only when there is at least one slice: an empty source would register a
            // model with no slices and show up in a host's discovery as a blank canvas.
            if (slices.Count > 0)
            {
                spc.AddSource(
                    "BobcatEventModelSource.g.cs",
                    EventModelEmitter.EmitSource(
                        compilation.AssemblyName ?? "Bobcat",
                        eventModelNameOf(compilation),
                        slices.Values.ToList(),
                        new EventModelEmitter.Contract
                        {
                            Query = compilation.GetTypeByMetadataName("JasperFx.Events.EventModeling.SlicePattern")?.GetMembers("Query").Length > 0,
                            RouteName = compilation.GetTypeByMetadataName("JasperFx.Events.EventModeling.PublisherOrigin")?.GetMembers("HttpRouteName").Length > 0,
                        }));
            }
        });
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
}

internal static class Diagnostics
{
    // Ids are never renumbered or reused; the next new one is BOBCAT041 (040 is taken by bobcat#610). BOBCAT001-022, 030, 031 and 035 belonged to the Gherkin
    // lane and were retired with it (bobcat#471); 025/026 went with the spec-ownership manifest
    // (issue #406); 033 is an MSBuild warning (buildTransitive/Bobcat.Generators.targets). A
    // diagnostic id a consumer has suppressed or searched for must keep meaning one thing.
    /// <summary>
    /// A <c>[BobcatStep]</c> template placeholder that names no parameter of the method
    /// (issue #339). Numbered from 027 deliberately: 023 and 024 are the slice-attribute
    /// diagnostics, and 025/026 are retired — they belonged to the <c>*.spec-ownership.yaml</c>
    /// manifest, which went with the curated authoring surface (issue #406). Neither number is
    /// reused: a diagnostic id a consumer has suppressed or searched for must keep meaning one
    /// thing.
    /// </summary>
    /// <remarks>
    /// Before #339 a placeholder survived for two different reasons — the argument was not a
    /// literal, or the name was wrong — and both looked identical on the canvas. Now the first
    /// binds at run time, so a surviving placeholder can only be the second, and it is worth
    /// saying out loud: nothing will ever fill it, and the step renders as a template forever.
    /// A warning rather than an error, because the step still records and the text still reads.
    /// </remarks>
    public static readonly DiagnosticDescriptor UnknownStepPlaceholder = new(
        "BOBCAT027",
        "Step template names no parameter",
        "Step attribute on '{0}' has the placeholder '{{{1}}}', but the method has no parameter named " +
        "'{1}'{2}. Nothing can fill it, so the step renders as '{{{1}}}'.",
        "Bobcat",
        DiagnosticSeverity.Warning,
        true);

    /// <summary>
    /// A <c>[BobcatFeature]</c> class that never opens a recording (issue #379).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ScenarioRecorder.Step</c> is null-conditional on the ambient recording, and only
    /// <c>[BobcatScenario]</c> opens one. Without it every step — a <c>[BobcatStep]</c>
    /// interceptor's and a marker comment's alike — records into <c>NoStep.Instance</c>, and the
    /// suite passes having produced no specification whatsoever.
    /// </para>
    /// <para>
    /// This is the third time the projected lane has been wired but not working, and the tell was
    /// identical every time: a green suite, a clean build, and no signal anywhere. The other two —
    /// 73 of 95 step texts rendering as raw placeholders (#339) and the scaffolder naming a base
    /// type the release had deleted (#376) — were both found by a human reading generated output
    /// weeks later. Nothing about a run can report this one, because a suite that records nothing
    /// is indistinguishable at run time from a suite with nothing to record. The compiler is the
    /// only place that can see it, which is why it is here rather than in the runtime.
    /// </para>
    /// <para>
    /// A warning, not an error: a hand-written adapter may open the scenario itself, and this
    /// matches by attribute name rather than by symbol so it also fires in a compilation that
    /// references no adapter at all.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor SpecRecordsNothing = new(
        "BOBCAT028",
        "Marked spec records no steps",
        "{0}",
        "Bobcat",
        DiagnosticSeverity.Warning,
        true);

    /// <summary>
    /// A comment that opens with <c>And</c> or <c>But</c> where no narrative is open: ordinary prose,
    /// reported so the author is never left wondering where their step went.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule it explains exists because English sentences begin "And …" and "But …" constantly, and
    /// a comment in a test body is overwhelmingly NOT a step. A keyword that can only continue a
    /// narrative must not be able to start one — otherwise switching the marker lane on silently turns
    /// a note to a reader into a specification step, and wraps the real steps underneath it.
    /// </para>
    /// <para>
    /// <b>Info, not a warning.</b> The overwhelmingly common case is that the comment really is prose
    /// and everything is fine; a warning would train people to ignore it. It is here for the one
    /// author who meant a step and cannot see why it is missing.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor ProseReadAsComment = new(
        "BOBCAT029",
        "Comment read as prose, not a step",
        "{0}",
        "Bobcat",
        DiagnosticSeverity.Info,
        true);

    /// <summary>
    /// A table column, or a partial object's member path, that names no member of the type it is about
    /// (bobcat#415, bobcat#420). Checked wherever the type and the text are both known at compile
    /// time: a constant table passed to a property check, a set verification,
    /// <c>PartialObjects.FromTable</c>, and a constant <c>Specified&lt;T&gt;.With</c> path. A stale column is always a broken spec, so this
    /// is an error rather than the <c>invalid</c> cell the next run would show.
    /// </summary>
    public static readonly DiagnosticDescriptor UnknownTableColumn = new(
        "BOBCAT032",
        "A table column does not match the type",
        "{0}",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    /// <summary>
    /// bobcat#500: a projected specification's HTTP act passes a <c>Route</c> / <c>RouteName</c> argument the
    /// generator cannot turn into a route — a helper call or a variable, a route name no endpoint attribute
    /// declares (a minimal API's <c>.WithName</c>), or no route at all.
    /// </summary>
    /// <remarks>
    /// Info, because the slice is still right: its trigger kind is stamped and only the route is missing. It is
    /// here so a missing route is never silent — before it, an interpolated route and a helper call both
    /// quietly left the canvas without one.
    /// </remarks>
    public static readonly DiagnosticDescriptor UnresolvedTriggerRoute = new(
        "BOBCAT038",
        "An HTTP act's route cannot be known at compile time",
        "{0}",
        "Bobcat",
        DiagnosticSeverity.Info,
        true);

    /// <summary>
    /// bobcat#462: an <c>[EventModelRole]</c>'s <c>From</c>, or an <c>[EventModelTrigger]</c>'s <c>Route</c> or
    /// <c>RouteName</c> (bobcat#500), names no parameter of the method it is on. Reported where the vocabulary is
    /// DECLARED, because at a call site in another assembly the mistake is only a role silently never stamped.
    /// </summary>
    public static readonly DiagnosticDescriptor UnknownRoleSource = new(
        "BOBCAT034",
        "An Event Model role names no parameter of its method",
        "{0}",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    /// <summary>
    /// bobcat#489: two projected tests in one compilation resolve to the same <c>{Feature}/{Scenario}</c>
    /// identity -- same-named methods in two classes bound to one feature, or two method names that
    /// read as the same sentence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An error, because nothing downstream can tell them apart.</b> Filtering by identity runs both
    /// or the wrong one, the resident runner's binding keeps one, the flake/duration ledger and Stoat's
    /// per-spec history merge two histories into one, and the Event Model lists one specification where
    /// there are two. The symptom when it was found was none of those: an unrelated test failed after a
    /// new test happened to take an existing one's name.
    /// </para>
    /// <para>
    /// Compared ordinally on the identity string the generator emits, through the same
    /// <c>MarkerSpecNaming</c> derivation, so it agrees with <c>SpecIdentity</c> at run time by
    /// construction. The run-time half -- identities the compiler cannot see together, such as specs
    /// in two assemblies, or this error suppressed -- is reported when the spec manifest is written.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor DuplicateSpecIdentity = new(
        "BOBCAT036",
        "Two projected specifications share one spec identity",
        "The spec identity '{0}' is also claimed by {1}. Identity filtering, the resident runner, the " +
        "flake/duration ledger and the Event Model would treat them as one specification. Rename one of " +
        "the test methods, or give one an explicit scenario name with [BobcatSpec(Name = \"...\")]. For two " +
        "data rows of one method, change or remove the repeated row, or give it its own label with " +
        "[BobcatExample(\"...\", ...)].",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    /// <summary>
    /// bobcat#484: the compilation has step-helper calls (or projected assertions) to intercept, but the
    /// project never opted <c>Bobcat.Generated</c> into C# interceptors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>Bobcat.Generators</c> package sets <c>InterceptorsNamespaces</c> from its
    /// <c>buildTransitive</c> props. A <c>ProjectReference</c> analyzer, the way this repository's own
    /// tests and samples consume the generator, takes no build assets, so it gets the generator and not
    /// the opt-in. What that cost used to be a CS9137 per call site in a generated file, and a project
    /// that silenced it some other way ran every <c>[Given]</c>/<c>[When]</c>/<c>[Then]</c> helper as an
    /// ordinary method call: no step, so a <c>SpecAssert</c> wrong landed nowhere and the spec passed.
    /// </para>
    /// <para>
    /// <b>An error, because the check cannot be wrong.</b> It reads the same parse option the compiler
    /// enforces (<c>InterceptorsNamespaces</c> or <c>InterceptorsPreviewNamespaces</c> arrive as
    /// <c>/features:</c>), and it only fires where interceptors would otherwise have been emitted and
    /// failed to compile. A project with nothing to intercept never sees it. Reported once, at the first
    /// call site, and no interceptor is emitted alongside it.
    /// </para>
    /// </remarks>
    public static readonly DiagnosticDescriptor InterceptorsNotEnabled = new(
        "BOBCAT037",
        "Bobcat's interceptors are not enabled in this project",
        "This project has {0} Bobcat step call{1} to intercept, but interceptors are not enabled for the " +
        "Bobcat.Generated namespace, so no step would record and a SpecAssert inside a step helper would report " +
        "to nothing. Add <InterceptorsNamespaces>$(InterceptorsNamespaces);Bobcat.Generated</InterceptorsNamespaces> " +
        "to the project. The Bobcat.Generators package does this for you; a ProjectReference to the generator " +
        "does not, so import its buildTransitive/Bobcat.Generators.props instead.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    /// <summary>
    /// bobcat#535: a step helper the generated interceptor cannot call — <c>protected</c>, <c>private</c>, or
    /// declared on a type the assembly cannot reach. An error for BOBCAT037's reason: left unintercepted the
    /// helper opens no step, so a <c>SpecAssert</c> inside it reports to nothing.
    /// </summary>
    public static readonly DiagnosticDescriptor InaccessibleStep = new(
        "BOBCAT039",
        "Step helper is not accessible to Bobcat's interceptor",
        "Step helper '{0}' is {1}, so Bobcat's generated interceptor cannot call it and its calls would record no " +
        "step. Make it public or internal.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    /// <summary>
    /// bobcat#600: an override of an inherited <c>[BobcatSpec]</c>/<c>[BobcatTheory]</c> test carries a
    /// different test attribute, usually <c>[Fact(Skip = "…")]</c> to skip one subclass's copy.
    /// </summary>
    /// <remarks>
    /// xUnit inherits the base method's <c>[BobcatSpec]</c>, so the override has two Fact-derived
    /// attributes and xUnit fails it at run time ("has multiple [Fact]-derived attributes") rather than
    /// skipping it. An error, because that run-time failure is certain; the fix is to keep the attribute
    /// family, <c>[BobcatSpec(Skip = "…")]</c>.
    /// </remarks>
    public static readonly DiagnosticDescriptor InheritedSpecOverriddenWithFact = new(
        "BOBCAT040",
        "An override of an inherited [BobcatSpec] test carries another test attribute",
        "This override inherits [BobcatSpec] from the method it overrides, so with this test attribute too xUnit " +
        "sees two Fact-derived attributes and fails the test at run time instead of running or skipping it. " +
        "Use [BobcatSpec(Skip = \"...\")] (or [BobcatTheory(...)]) on the override instead.",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor SliceBindingConflict = new(
        "BOBCAT023",
        "[BobcatSlice] names two different slices",
        "{0}",
        "Bobcat",
        DiagnosticSeverity.Error,
        true);

    public static readonly DiagnosticDescriptor PreferSliceType = new(
        "BOBCAT024",
        "Prefer SliceType over a literal SliceName",
        "{0}",
        "Bobcat",
        DiagnosticSeverity.Warning,
        true);
}
