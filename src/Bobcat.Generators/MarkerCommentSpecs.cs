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
/// Issue #110: a specification written as an ordinary xUnit or TUnit test, whose steps are
/// declared by <b>marker comments</b> rather than by a fluent API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a generator, and not a runtime hook.</b> Comments are erased by the compiler — nothing
/// at runtime can see <c>// Given a proposed appointment</c>. Every other authoring style Bobcat
/// supports could in principle be recorded as it executes; this one cannot, so reading the syntax
/// tree is not a design preference here, it is the only place the information exists.
/// </para>
/// <para>
/// <b>What the test body is.</b> Ordinary code that runs under the team's own runner: breakpoints
/// work, there are no lambdas, and the "compose, then execute" split that <c>Specification</c>
/// needs does not apply. The generator contributes the <i>rendering</i> — the ordered steps and
/// the <c>{Feature}/{Scenario}</c> identity — and the runner contributes the verdict.
/// </para>
/// <para>
/// <b>Minimal edits, deliberately.</b> The target is an existing xUnit suite (Marten's
/// DaemonTests is the first real subject), so opting in is one class-level attribute plus
/// comments. No base class, no method signature change, no restructuring — anything more and a
/// large existing suite cannot adopt it incrementally.
/// </para>
/// </remarks>
internal static class MarkerCommentSpecs
{
    internal const string AttributeName = "BobcatFeature";

    /// <summary>The step keywords a marker comment MAY open with, longest first so "And" inside
    /// a sentence cannot be mistaken for a keyword. A comment may also open with <c>*</c> and carry
    /// no keyword at all (issue #324).</summary>
    private static readonly string[] Keywords = { "Given", "When", "Then", "And", "But" };

    internal sealed class MarkedSpec
    {
        public string FeatureTitle = "";

        /// <summary>
        /// The fully qualified test class, spelled the way a test framework's class/method filter
        /// expects it — a nested class joined with <c>+</c> (issue #391). Recorded rather than
        /// derived: the identity is a one-way function of these names, and an explicit
        /// <c>[BobcatFeature("…")]</c> title has no relationship to the class name at all.
        /// </summary>
        public string TestClass = "";

        /// <summary>
        /// An abstract class (bobcat#600). Its tests only ever run as a subclass's, under the
        /// subclass's identity, so it registers the steps its bodies declare but binds no identity of
        /// its own: listed, it would be a specification nothing can run.
        /// </summary>
        public bool IsAbstract;

        /// <summary>
        /// <c>typeof</c> expressions naming a base class in another assembly that this class inherits
        /// tests from (bobcat#600). The generated initializer runs that assembly's module initializer
        /// first, so the steps its bodies declare are registered before anything reads them.
        /// </summary>
        public readonly List<string> BaseModules = new();

        public readonly List<MarkedScenario> Scenarios = new();

        /// <summary>
        /// The class-level <c>[BobcatSlice]</c>, rendered as tag strings (issue #324) — so
        /// <c>GeneratorSliceTags</c> stays the one parser and the attribute is a typed front end over
        /// it rather than a second vocabulary.
        /// </summary>
        public readonly List<string> Tags = new();

        /// <summary>
        /// The class-level <c>TriggeredBy</c> on <c>[BobcatSlice]</c> (bobcat#463) — the fallback a
        /// method's own loses to.
        /// </summary>
        public string? TriggeredBy;

        /// <summary>What was wrong with a <c>[BobcatSlice]</c>, reported by the generator.</summary>
        public readonly List<MarkedProblem> Problems = new();
    }

    /// <summary>A diagnostic the extractor found. Reported where the generator has a
    /// <c>SourceProductionContext</c>; carried here because the extractor does not.</summary>
    internal sealed class MarkedProblem
    {
        public string Id = "";
        public string Message = "";
        public bool IsError;
        public Location? Where;
    }

    internal sealed class MarkedScenario
    {
        public string Title = "";

        /// <summary>The test method's own name, for the filter that runs just this one.</summary>
        public string TestMethod = "";

        /// <summary>
        /// The method's name in source, where BOBCAT036 points when this scenario's identity is
        /// shared with another's (bobcat#489).
        /// </summary>
        public Location? Where;

        /// <summary>
        /// Whether <c>[BobcatScenario]</c> opens a recording around this test — on the class, or on
        /// the method itself. It is what makes the test a specification at all, and therefore what
        /// decides whether its identity belongs in a listing (issue #391).
        /// </summary>
        public bool OpensRecording;

        /// <summary>
        /// Whether the method itself carries <c>[BobcatSpec]</c> / <c>[BobcatTheory]</c> — a test its author declared a
        /// specification, so one that reaches no slice is worth saying so about (BOBCAT041, bobcat#615).
        /// </summary>
        public bool DeclaredAsSpec;

        /// <summary>
        /// The identity the test body has under the base class that declares it, for a test this
        /// class inherits (bobcat#600), else null. Its declared and planned steps are registered there.
        /// </summary>
        public string? InheritedFrom;

        public readonly List<MarkedStep> Steps = new();

        /// <summary>The method-level <c>[BobcatSlice]</c>, which wins over the class's for this
        /// test — how one class covers several slices.</summary>
        public readonly List<string> Tags = new();

        /// <summary>The method-level <c>TriggeredBy</c> (bobcat#463), which wins over the class's.</summary>
        public string? TriggeredBy;

        /// <summary>
        /// The Event Modeling roles the test body states through role-stamped vocabulary calls
        /// (bobcat#462).
        /// </summary>
        public ProjectedRoles.Reading Roles = new();

        /// <summary>
        /// The scenario vocabulary calls in the test body (bobcat#625), for the preview only — they are
        /// not planned steps, because nothing numbers them for the recorder.
        /// </summary>
        public List<VocabularyCalls.Call> Vocabulary = new();

        /// <summary>
        /// <c>[BobcatSpec(…, Pending = true)]</c>: declared before the behaviour it describes
        /// (issue #404), so the slice gets a <c>PendingSpecification</c> hotspot rather than a
        /// specification.
        /// </summary>
        /// <remarks>
        /// <b>Explicit, and NOT inferred from having no steps.</b> The removed Gherkin lane read a
        /// step-less scenario as the pending case, because a scenario with no steps said nothing. A
        /// projected test with no marker comments says plenty: it runs real code and publishes a
        /// real verdict, and 33 of this repository's own 41 projected samples declare no marker
        /// steps at all. Reading "no steps" as "pending" would have turned most of a working suite
        /// into open questions on the canvas.
        /// </remarks>
        public bool Pending;

        /// <summary>
        /// The test's constant data rows (bobcat#569), each its own specification at run time and
        /// listed in the manifest under <c>{Feature}/{Scenario} {row}</c>. Empty for a test that is
        /// not data-driven, or whose rows only a run produces.
        /// </summary>
        public IReadOnlyList<SpecRowConstants.Row> Rows = Array.Empty<SpecRowConstants.Row>();

        /// <summary>A test with no marker comments at all: it runs, but it renders as nothing.</summary>
        public bool IsUnmarked => Steps.Count == 0;
    }

    internal sealed class MarkedStep
    {
        public string Keyword = "";
        public string Text = "";

        /// <summary>Where the comment is, for a diagnostic that needs to point at it.</summary>
        public Location? Where;

        /// <summary>1-based line of the comment, kept so a failure can later be mapped back to the
        /// step it fell inside — the open question on #110, and cheap to carry now.</summary>
        public int Line;
    }

    public static MarkedSpec? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var declaration = (ClassDeclarationSyntax)ctx.Node;
        var title = featureAttributeTitle(declaration, ctx.SemanticModel, ct, out var marked);
        if (!marked) return null;

        var symbol = ctx.SemanticModel.GetDeclaredSymbol(declaration, ct);
        var spec = new MarkedSpec
        {
            FeatureTitle = MarkerSpecNaming.FeatureTitle(declaration.Identifier.Text, title),
            TestClass = FilterNameOf(symbol),
            IsAbstract = symbol?.IsAbstract == true
        };

        spec.Tags.AddRange(sliceTags(declaration, ctx.SemanticModel, spec.Problems));
        spec.TriggeredBy = triggeredBy(declaration);

        // Issue #379. A marked class renders nothing unless something OPENS a recording:
        // ScenarioRecorder.Step is null-conditional on the ambient one, so with no scenario every
        // [BobcatStep] interceptor and every marker comment records into NoStep.Instance and the
        // suite goes green having produced no specification at all. [BobcatFeature] and
        // [BobcatSlice] carry the BINDING; only [BobcatScenario] starts the recording.
        //
        // Checked here rather than at runtime because at runtime there is nothing to check: a suite
        // that records nothing looks exactly like a suite with no steps to record.
        //
        // [BobcatFeature] alone opens it too when the assembly carries Bobcat.Xunit's
        // [assembly: RecordBobcatFeatures] hook, which the package applies by default — so the
        // generator's idea of "records" has to follow the runtime's, or the listing would drop
        // every one-attribute feature class.
        var featuresRecord = RecordsFeaturesAssemblyWide(ctx.SemanticModel.Compilation);
        var opensRecording = featuresRecord
                             || HasScenarioAttribute(declaration)
                             || declaration.Members.OfType<MethodDeclarationSyntax>().Any(HasScenarioAttribute);

        foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (!IsTestMethod(method)) continue;

            var scenario = new MarkedScenario
            {
                Title = ScenarioTitleOf(method, ctx.SemanticModel, ct),
                TestMethod = method.Identifier.Text,
                Where = method.Identifier.GetLocation(),
                OpensRecording = featuresRecord || HasScenarioAttribute(declaration) || HasScenarioAttribute(method),
                DeclaredAsSpec = method.AttributeLists.SelectMany(list => list.Attributes)
                    .Any(a => IsSpecAttribute(ShortName(a.Name.ToString()))),
                Pending = isPending(method),
                Rows = SpecRowConstants.Read(method, ctx.SemanticModel, ct)
            };

            var prose = new List<MarkedStep>();
            foreach (var step in StepsIn(method, prose)) scenario.Steps.Add(step);

            foreach (var skipped in prose)
            {
                spec.Problems.Add(new MarkedProblem
                {
                    Id = "ProseKeyword",
                    IsError = false,
                    Where = skipped.Where,
                    Message =
                        $"The comment \"{skipped.Keyword} {skipped.Text}\" opens with '{skipped.Keyword}', but no "
                        + "step has opened the narrative in this test, so it is read as an ordinary comment rather "
                        + "than a step. 'And' and 'But' continue a narrative and cannot start one. Open with Given, "
                        + "When or Then, or mark it '* " + skipped.Text + "' if it is a step with no keyword."
                });
            }
            scenario.Tags.AddRange(sliceTags(method, ctx.SemanticModel, spec.Problems));
            scenario.TriggeredBy = triggeredBy(method);
            scenario.Roles = ProjectedRoles.Read(method, ctx.SemanticModel, ct);
            scenario.Vocabulary = VocabularyCalls.Read(method, ctx.SemanticModel, ct);

            // bobcat#500: an HTTP act whose route cannot be known at all says so (BOBCAT038, info)
            if (scenario.Roles.RouteProblem is { } routeProblem)
            {
                spec.Problems.Add(new MarkedProblem
                {
                    Id = "UnresolvedRoute",
                    IsError = false,
                    Where = scenario.Roles.RouteProblemLocation,
                    Message = routeProblem
                });
            }

            spec.Scenarios.Add(scenario);
        }

        if (spec.Scenarios.Count == 0) return null;

        // Only a class that BINDS A SLICE. [BobcatFeature] on its own is a legitimate compile-time
        // use — Bobcat's own acceptance tests carry it to make the generator emit their declared
        // steps, then assert on DeclaredSteps without ever running a scenario, and warning at them
        // would be wrong. [BobcatSlice] is the stronger claim: this behaviour is specified HERE and
        // appears on the Event Model. That claim is false if nothing records.
        //
        // The narrower rule came from running the first version over this repository, where it
        // fired on four such classes. Four false positives in the first compilation it met is the
        // kind of thing a diagnostic has to be measured against rather than reasoned about.
        var bindsASlice = spec.Tags.Count > 0 || spec.Scenarios.Any(x => x.Tags.Count > 0);

        if (bindsASlice && !opensRecording)
        {
            spec.Problems.Add(new MarkedProblem
            {
                Id = "RecordsNothing",
                Message =
                    $"'{declaration.Identifier.Text}' binds a slice with [BobcatSlice] but nothing opens a " +
                    "scenario, so none of its steps are recorded and it reaches the Event Model as no " +
                    "specification at all. Add [BobcatScenario] to the class (from Bobcat.Xunit, Bobcat.TUnit, " +
                    "Bobcat.NUnit or Bobcat.MSTest, whichever runner the suite uses), or on an xUnit suite replace [Fact] + [BobcatSlice] with one " +
                    "[BobcatSpec(typeof(TheSlice))] on each test, which does both and cannot come apart.",
                Where = declaration.Identifier.GetLocation()
            });
        }

        return spec;
    }

    /// <summary>
    /// Does this class or method open the recording its steps go into — <c>[BobcatScenario]</c>,
    /// or the <c>[BobcatSpec]</c> that implies it?
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched by short name, the way <c>[BobcatSlice]</c> is: the xUnit and TUnit adapters ship
    /// the same attribute under their own namespaces, and a suite may write it qualified. Matching
    /// the name rather than resolving the symbol also means this works in a compilation that
    /// references neither adapter — which is exactly the compilation worth warning.
    /// </para>
    /// <para>
    /// <b><c>[BobcatSpec]</c> counts, and matching by name is why that needed saying out loud</b>
    /// (issue #403). It implements the same bracket at run time, but a name check for
    /// "BobcatScenario" sees nothing in <c>BobcatSpec</c>, so a suite written in the one-attribute
    /// form would have tripped BOBCAT028 — "binds a slice but nothing opens a scenario" — about a
    /// suite that records perfectly well. Every name check this attribute stands in for is listed
    /// in <see cref="ScenarioOpeningAttributes"/>.
    /// </para>
    /// </remarks>
    internal static bool HasScenarioAttribute(MemberDeclarationSyntax member)
        => member.AttributeLists
            .SelectMany(list => list.Attributes)
            .Any(a => ScenarioOpeningAttributes.Contains(ShortName(a.Name.ToString())));

    /// <summary>
    /// Does this assembly carry the runner hook that makes <c>[BobcatFeature]</c> open a recording on
    /// its own — <c>[assembly: Bobcat.Xunit.RecordBobcatFeatures]</c>? Matched by name like every
    /// other adapter attribute here, so the generator never references a runner.
    /// </summary>
    internal static bool RecordsFeaturesAssemblyWide(Compilation compilation)
        => compilation.Assembly.GetAttributes()
            .Any(a => a.AttributeClass?.Name is "RecordBobcatFeaturesAttribute" or "RecordBobcatFeatures");

    /// <summary>
    /// The attributes that open a scenario recording. <c>[BobcatSpec]</c> is here because it
    /// IMPLIES <c>[BobcatScenario]</c> (issue #403) — it runs the same bracket.
    /// </summary>
    internal static readonly string[] ScenarioOpeningAttributes = { "BobcatScenario", SpecAttributeName, TheoryAttributeName };

    /// <summary>The one-attribute projected spec (issue #403): a Fact, a scenario and a slice
    /// binding at once.</summary>
    internal const string SpecAttributeName = "BobcatSpec";

    /// <summary>
    /// The one-attribute data-driven spec (bobcat#569): <c>[BobcatSpec]</c> for a theory, with the same
    /// settings, so every reader of <see cref="SpecAttributeName"/> reads it too.
    /// </summary>
    internal const string TheoryAttributeName = "BobcatTheory";

    /// <summary><c>[BobcatSpec]</c> or <c>[BobcatTheory]</c>, by short name.</summary>
    internal static bool IsSpecAttribute(string shortName) => shortName is SpecAttributeName or TheoryAttributeName;

    /// <summary>
    /// The slice binding on a class or a method — <c>[BobcatSlice]</c>, or the <c>[BobcatSpec]</c>
    /// that carries the same settings (issues #324, #403) — rendered as tag strings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rendered as tags rather than carried as its own shape so <see cref="GeneratorSliceTags"/>
    /// keeps being the single parser: the attribute is a typed front end over that vocabulary, not a
    /// second one to keep in step.
    /// </para>
    /// <para>
    /// <b>Both attributes are read, not one in preference to the other.</b> They are the same
    /// settings under two spellings, so a method carrying both is one binding stated twice — and
    /// the existing BOBCAT023 rule already says what to do when two spellings disagree: refuse,
    /// because one of them is wrong and no silent winner is the right answer. Preferring one
    /// instead would make the loser's settings vanish without a word.
    /// </para>
    /// <para>
    /// <b><c>[BobcatSpec(typeof(X))]</c> states the slice positionally</b>, which is the whole
    /// ergonomic point of #403, so this reads an unnamed <c>typeof(…)</c> argument as
    /// <c>SliceType</c>. Only a <c>typeof</c>: the constructor's other parameters are the
    /// <c>[CallerFilePath]</c>/<c>[CallerLineNumber]</c> pair, which nobody writes by hand, and
    /// requiring the <c>typeof</c> shape means a hand-written one cannot be mistaken for a slice.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> sliceTags(
        MemberDeclarationSyntax node, SemanticModel model, List<MarkedProblem> problems)
    {
        var attributes = node.AttributeLists
            .SelectMany(list => list.Attributes)
            .Where(a => ShortName(a.Name.ToString()) is "BobcatSlice" || IsSpecAttribute(ShortName(a.Name.ToString())))
            .ToList();

        if (attributes.Count == 0) yield break;

        string? fromName = null, fromType = null;
        AttributeSyntax? where = null;
        var tags = new List<string>();

        foreach (var attribute in attributes)
        {
            where ??= attribute;
            if (attribute.ArgumentList is null) continue;

            var positional = IsSpecAttribute(ShortName(attribute.Name.ToString()));

            foreach (var argument in attribute.ArgumentList.Arguments)
            {
                var member = argument.NameEquals?.Name.Identifier.Text;

                // [BobcatSpec(typeof(X))]: the slice type, stated positionally.
                if (member is null)
                {
                    if (positional && typeNameOf(argument.Expression, model) is { Length: > 0 } named)
                    {
                        fromType ??= named;
                        where = attribute;
                    }

                    continue;
                }

                switch (member)
                {
                    case "SliceName":
                        fromName ??= literalOf(argument.Expression);
                        if (fromName is not null) where = attribute;
                        break;
                    case "SliceType":
                        fromType ??= typeNameOf(argument.Expression, model);
                        if (fromType is not null) where = attribute;
                        break;
                    case "Domain":
                    case "Chapter":
                    case "Pattern":
                        if (literalOf(argument.Expression) is { Length: > 0 } value)
                        {
                            var tag = $"{member.ToLowerInvariant()}:{value}";
                            if (!tags.Contains(tag)) tags.Add(tag);
                        }

                        break;
                }
            }
        }

        // Both set and disagreeing is an error rather than a precedence rule: one of the two is
        // wrong and no silent winner is the right answer.
        if (fromName is { Length: > 0 } && fromType is { Length: > 0 } && fromName != fromType)
        {
            problems.Add(new MarkedProblem
            {
                Id = "BOBCAT023",
                IsError = true,
                Where = (where ?? attributes[0]).GetLocation(),
                Message =
                    $"SliceName = \"{fromName}\" and SliceType = typeof({fromType}) name different slices. "
                    + "SliceType means exactly SliceName = type.Name — set one of them."
            });
        }

        var slice = fromType ?? fromName;
        if (slice is { Length: > 0 })
        {
            // Nudge a literal string toward the type when one of that name exists: a type survives
            // to the generator where a string does not, so only SliceType can be cross-checked
            // against the model later.
            if (fromType is null && model.Compilation.GetSymbolsWithName(slice, SymbolFilter.Type).Any())
            {
                problems.Add(new MarkedProblem
                {
                    Id = "BOBCAT024",
                    IsError = false,
                    Where = (where ?? attributes[0]).GetLocation(),
                    Message =
                        $"[{ShortName((where ?? attributes[0]).Name.ToString())}(SliceName = \"{slice}\")] names a "
                        + "slice that IS a type in this compilation. "
                        + $"Prefer SliceType = typeof({slice}): it is rename-safe the same way and the type survives "
                        + "to the generator, where a string cannot be checked against the model."
                });
            }

            yield return $"{GeneratorSliceTags.SlicePrefix}{slice}";
        }

        foreach (var tag in tags) yield return tag;
    }

    /// <summary>
    /// <c>Pending = true</c> on this method's <c>[BobcatSpec]</c> (issue #404).
    /// </summary>
    /// <remarks>
    /// Read with its own reader rather than through the slice-tag vocabulary, because pending is a
    /// property of one SPECIFICATION and the tags are slice grouping. Routing it through
    /// <see cref="GeneratorSliceTags"/> would have meant a new tag to carry something no slice has an
    /// opinion about.
    /// </remarks>
    private static bool isPending(MemberDeclarationSyntax node)
        => node.AttributeLists
            .SelectMany(list => list.Attributes)
            .Where(a => IsSpecAttribute(ShortName(a.Name.ToString())))
            .SelectMany(a => a.ArgumentList?.Arguments ?? default)
            .Any(argument =>
                argument.NameEquals?.Name.Identifier.Text == "Pending"
                && argument.Expression.IsKind(SyntaxKind.TrueLiteralExpression));

    /// <summary>
    /// <c>TriggeredBy = "…"</c> on this member's <c>[BobcatSlice]</c> or <c>[BobcatSpec]</c> (bobcat#463).
    /// Read on its own rather than as a tag, like <see cref="isPending"/>: it is prose for the canvas, and
    /// routing it through <see cref="GeneratorSliceTags"/> would add a tag to carry something no slice
    /// grouping reads.
    /// </summary>
    private static string? triggeredBy(MemberDeclarationSyntax node)
        => node.AttributeLists
            .SelectMany(list => list.Attributes)
            .Where(a => ShortName(a.Name.ToString()) is "BobcatSlice" || IsSpecAttribute(ShortName(a.Name.ToString())))
            .SelectMany(a => a.ArgumentList?.Arguments ?? default)
            .Where(argument => argument.NameEquals?.Name.Identifier.Text == "TriggeredBy")
            .Select(argument => literalOf(argument.Expression)?.Trim())
            .FirstOrDefault(label => label is { Length: > 0 });

    /// <summary>
    /// The scenario title of a projected test method: its <c>[BobcatSpec(Name = "…")]</c> when it
    /// states one (bobcat#489), else the method name read as a sentence. The one place the generator
    /// reads a scenario title, so the declared steps, the binding, the planned steps, the Event Model
    /// and the specification bindings all agree, and so does BOBCAT036.
    /// </summary>
    internal static string ScenarioTitleOf(MethodDeclarationSyntax method, SemanticModel model, CancellationToken ct)
        => MarkerSpecNaming.ScenarioTitle(method.Identifier.ValueText, explicitNameOf(method, model, ct));

    /// <summary>A scenario's <c>{Feature}/{Scenario}</c> identity, as everything this generator emits spells it.</summary>
    internal static string IdentityOf(MarkedSpec spec, MarkedScenario scenario)
        => MarkerSpecNaming.Identity(spec.FeatureTitle, scenario.Title);

    /// <summary>One constant row's identity (bobcat#569): the scenario's, with the row part on the end.</summary>
    internal static string IdentityOf(MarkedSpec spec, MarkedScenario scenario, SpecRowConstants.Row row)
        => MarkerSpecNaming.Identity(spec.FeatureTitle, MarkerSpecNaming.RowTitle(scenario.Title, row.Part));

    /// <summary>
    /// <c>Name = "…"</c> on this method's <c>[BobcatSpec]</c> (bobcat#489). Read with the semantic model
    /// so a <c>const</c> or a <c>nameof</c> works, with the literal as the fallback for a compilation
    /// that cannot bind the attribute — matched by name, like <see cref="isPending"/>, because the
    /// generator references no runner.
    /// </summary>
    private static string? explicitNameOf(MethodDeclarationSyntax method, SemanticModel model, CancellationToken ct)
        => method.AttributeLists
            .SelectMany(list => list.Attributes)
            .Where(a => IsSpecAttribute(ShortName(a.Name.ToString())))
            .SelectMany(a => a.ArgumentList?.Arguments ?? default)
            .Where(argument => argument.NameEquals?.Name.Identifier.Text == "Name")
            .Select(argument => model.GetConstantValue(argument.Expression, ct).Value as string
                                ?? literalOf(argument.Expression))
            .FirstOrDefault(name => name is { } value && value.Trim().Length > 0);

    /// <summary>
    /// Every identity two or more projected tests in this compilation share (bobcat#489), with each
    /// test that claims it. Ordinal and exact, the way <c>SpecIdentity</c> compares at run time: two
    /// identities differing only in case are two specifications, and two method names that read as the
    /// same sentence (<c>order_is_placed</c> and <c>order__is_placed</c>) are one.
    /// </summary>
    /// <remarks>
    /// Every test method of a <c>[BobcatFeature]</c> class counts, whether or not it records or declares
    /// steps: the attribute says its methods project into the model, and the Event Model, the
    /// specification bindings and a run all key on the identity regardless.
    /// </remarks>
    internal static IReadOnlyList<DuplicateIdentity> DuplicateIdentities(IEnumerable<MarkedSpec> specs)
        => specs
            // An abstract class's tests never run under its identity, only under each subclass's
            // (bobcat#600), and those are what is compared.
            .Where(spec => !spec.IsAbstract)
            .SelectMany(spec => spec.Scenarios.SelectMany(scenario =>
                // bobcat#569: each constant row is a specification of its own, so two rows of one
                // method that format alike (or share a label) claim one identity, pointed at the row.
                new[] { (Identity: IdentityOf(spec, scenario), Spec: spec, Scenario: scenario, Where: scenario.Where) }
                    .Concat(scenario.Rows.Select(row =>
                        (Identity: IdentityOf(spec, scenario, row), Spec: spec, Scenario: scenario, Where: row.Where ?? scenario.Where)))))
            .GroupBy(x => x.Identity, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => new DuplicateIdentity(
                group.Key,
                group.Select(x => new DuplicateClaim(x.Spec.TestClass, x.Scenario.TestMethod, x.Where)).ToList()))
            .ToList();

    internal sealed class DuplicateIdentity
    {
        public DuplicateIdentity(string identity, IReadOnlyList<DuplicateClaim> claims)
        {
            Identity = identity;
            Claims = claims;
        }

        public string Identity { get; }
        public IReadOnlyList<DuplicateClaim> Claims { get; }
    }

    internal sealed class DuplicateClaim
    {
        public DuplicateClaim(string testClass, string testMethod, Location? where)
        {
            TestClass = testClass;
            TestMethod = testMethod;
            Where = where;
        }

        public string TestClass { get; }
        public string TestMethod { get; }
        public Location? Where { get; }

        /// <summary><c>Ns.Class.method</c>, with the file and line when the location is in source.</summary>
        public string Describe()
        {
            var name = TestClass.Length > 0 ? TestClass + "." + TestMethod : TestMethod;
            if (Where is null || !Where.IsInSource) return name;

            var span = Where.GetLineSpan();
            return $"{name} ({System.IO.Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1})";
        }
    }

    private static string? literalOf(ExpressionSyntax expression)
        => expression is LiteralExpressionSyntax { Token.Value: string text } ? text : null;

    /// <summary>
    /// <c>typeof(X)</c> → <c>"X"</c>. The symbol when the model can bind it, else the right-most
    /// identifier written — the generator recognizes things by name and must not fail on a type it
    /// cannot resolve.
    /// </summary>
    private static string? typeNameOf(ExpressionSyntax expression, SemanticModel model)
    {
        if (expression is not TypeOfExpressionSyntax typeOf) return null;

        if (model.GetSymbolInfo(typeOf.Type).Symbol is INamedTypeSymbol symbol) return symbol.Name;

        return typeOf.Type switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            _ => null,
        };
    }

    /// <summary>
    /// The short names (no namespace, no <c>Attribute</c> suffix) of every attribute that makes a
    /// method a test: xUnit's <c>Fact</c>/<c>Theory</c>, TUnit's and NUnit's <c>Test</c>, NUnit's
    /// <c>TestCase</c>, MSTest's <c>TestMethod</c>/<c>DataTestMethod</c> (issue #386), and
    /// <c>[BobcatSpec]</c>.
    /// </summary>
    /// <remarks>
    /// A set rather than an inline pattern so a test can pin the whole of it: MSTest's spellings
    /// were missing for months because an append-only list gives nobody a reason to look at what
    /// is already there.
    /// </remarks>
    internal static readonly IReadOnlyCollection<string> TestMethodAttributeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestCase", "TestMethod", "DataTestMethod", SpecAttributeName, TheoryAttributeName
    };

    /// <summary>
    /// Any method a test runner would call. Matched on attribute NAME rather than on a symbol,
    /// so the generator needs no reference to xUnit, TUnit, NUnit or MSTest — Bobcat cannot depend
    /// on a runner it is trying to be neutral about.
    /// </summary>
    /// <remarks>
    /// <c>BobcatSpec</c> is in the list because it <em>is</em> a <c>FactAttribute</c> subclass
    /// (issue #403) and xUnit discovers it as one. Matching by name is what makes that a separate
    /// fact to record rather than something inheritance handles: the generator never sees the base
    /// type.
    /// </remarks>
    internal static bool IsTestMethod(MethodDeclarationSyntax method)
        => method.AttributeLists
            .SelectMany(list => list.Attributes)
            .Select(a => ShortName(a.Name.ToString()))
            .Any(TestMethodAttributeNames.Contains);

    /// <summary>
    /// The marker comments in a method body, in source order.
    /// </summary>
    /// <remarks>
    /// Read from the leading trivia of each statement rather than by scanning the file for
    /// comment tokens: it keeps the steps ordered by the statement they introduce, ignores a
    /// comment trailing on the same line as code, and means a comment inside a nested block still
    /// belongs to the statement it precedes.
    /// </remarks>
    internal static IEnumerable<MarkedStep> StepsIn(MethodDeclarationSyntax method)
        => StepsIn(method, null);

    /// <param name="prose">
    /// Collects the comments that LOOKED like steps and were read as prose instead, so the caller can
    /// say so out loud (BOBCAT029). Null when nobody is reporting.
    /// </param>
    internal static IEnumerable<MarkedStep> StepsIn(MethodDeclarationSyntax method, List<MarkedStep>? prose)
    {
        if (method.Body is null) yield break;

        // Whether a Given/When/Then/* comment has opened the narrative yet in THIS method. `And` and
        // `But` continue a narrative; they cannot start one.
        var narrativeOpen = false;

        foreach (var trivia in method.Body.DescendantTrivia())
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)) continue;

            var step = Parse(trivia.ToString());
            if (step is null) continue;

            step.Line = trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            step.Where = trivia.GetLocation();

            // An `And`/`But` with nothing to continue is ORDINARY PROSE, and this is the rule that
            // makes the marker lane safe to switch on. English sentences begin "And ..." and "But ..."
            // all the time — the author of this very rule wrote `// And a false one fails its step`
            // as a note to a reader and had it silently become a step, wrapping the two real steps
            // under a narrative row that was never meant to exist. A keyword that can only continue
            // something cannot be the thing that starts it.
            if (isContinuation(step.Keyword) && !narrativeOpen)
            {
                prose?.Add(step);
                continue;
            }

            if (!isContinuation(step.Keyword) && step.Keyword != "Note") narrativeOpen = true;

            yield return step;
        }
    }

    /// <summary>A keyword that continues the narrative it is in rather than opening one.</summary>
    private static bool isContinuation(string keyword)
        => keyword is "And" or "But";

    /// <summary>
    /// <c>// Given a proposed appointment</c> → Given / "a proposed appointment". Anything that
    /// does not open with a keyword is an ordinary comment and stays invisible — a test is full of
    /// those, and treating them as steps would make the rendering worse than nothing.
    /// </summary>
    internal static MarkedStep? Parse(string comment)
    {
        // Whitespace BEFORE the slashes, then the slashes, then whitespace after: a comment read
        // from trivia carries its indentation, so trimming slashes first strips nothing at all.
        var text = comment.Trim().TrimStart('/').Trim();
        if (text.Length == 0) return null;

        // A bullet is a step with NO keyword (issue #324). Bobcat's rendering does not depend on
        // Given/When/Then and should not — the inspiration here is Gauge, whose specs are bulleted
        // sentences, and Storyteller, whose specifications read as prose rather than as a keyword
        // table. Plenty of steps are simply not one of five words.
        //
        // It has to be marked, though: an ordinary comment in a test body is overwhelmingly NOT a
        // step, so treating every comment as one would bury the real steps in noise. `*` is one
        // character of opt-in, unmistakably deliberate, and the same character Gauge uses.
        if (text[0] == '*')
        {
            var bulleted = text.Substring(1).Trim();
            return bulleted.Length == 0 ? null : new MarkedStep { Keyword = "", Text = bulleted };
        }

        // `// Note: …` is text for the reader rather than a step — rendered, never judged, and never
        // part of the Given/When/Then narrative. The colon is required: "Note that…" in an ordinary
        // comment stays an ordinary comment.
        if (text.StartsWith("Note:", StringComparison.Ordinal))
        {
            var note = text.Substring("Note:".Length).Trim();
            return note.Length == 0 ? null : new MarkedStep { Keyword = "Note", Text = note };
        }

        foreach (var keyword in Keywords)
        {
            if (!text.StartsWith(keyword, StringComparison.Ordinal)) continue;

            // "Givenchy" is not a step. The keyword has to be a word on its own.
            if (text.Length > keyword.Length && !char.IsWhiteSpace(text[keyword.Length])) continue;

            var remainder = text.Substring(keyword.Length).Trim();
            if (remainder.Length == 0) return null;

            return new MarkedStep { Keyword = keyword, Text = remainder };
        }

        return null;
    }


    /// <summary>
    /// The registration a marked assembly carries: every scenario's declared steps, in order,
    /// keyed by the identity the recorder will ask for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A module initializer, so opting in really is only comments.</b> The steps have to reach
    /// the runtime before the first test runs, and nothing in the test body can be made to carry
    /// them. Anything else — a call in a fixture, an attribute per method, a registration the
    /// author remembers — would defeat the point of an authoring style whose entire promise is
    /// that an existing suite adopts it by writing sentences.
    /// </para>
    /// <para>
    /// Emitted only when at least one class is marked, so an assembly that never heard of #110
    /// gains no initializer and no startup cost.
    /// </para>
    /// </remarks>
    public static string Emit(IEnumerable<MarkedSpec> specs) => Emit(specs, framework: "");

    /// <summary>
    /// <paramref name="framework"/> is <see cref="FrameworkOf"/>'s answer, baked in so a listing
    /// request can say which runner owns the process it is describing (issue #391).
    /// </summary>
    public static string Emit(IEnumerable<MarkedSpec> specs, string framework)
        => Emit(specs, framework, null);

    /// <summary>
    /// <paramref name="coordinates"/> is <see cref="EventModelEmitter.Coordinates"/>'s answer (bobcat#573): each
    /// identity's slice, chapter, domain and pattern, bound beside its test method so the manifest and the runner
    /// adapters' traits read one record.
    /// </summary>
    public static string Emit(IEnumerable<MarkedSpec> specs, string framework,
        IReadOnlyDictionary<string, (string Slice, string? Chapter, string? Domain, string? Pattern)>? coordinates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine($"namespace {StepInterceptors.Namespace}");
        sb.AppendLine("{");
        sb.AppendLine("    internal static class BobcatDeclaredStepRegistration");
        sb.AppendLine("    {");
        sb.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("        internal static void Register()");
        sb.AppendLine("        {");

        var specList = specs.ToList();

        // bobcat#600: a base class compiled into another assembly registers its bodies' steps from
        // that assembly's own initializer, which the runtime only runs once something touches the
        // module. Run it now, so a subclass's steps are there for a preview or a listing.
        foreach (var module in specList.SelectMany(spec => spec.BaseModules).Distinct())
        {
            sb.AppendLine(
                $"            global::System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof({module}).Module.ModuleHandle);");
        }

        foreach (var spec in specList)
        {
            foreach (var scenario in spec.Scenarios)
            {
                var identity = IdentityOf(spec, scenario);

                // Issue #391. The identity cannot be inverted back to a method — Prettify reads
                // underscores as spaces and strips a Specs suffix, and an explicit
                // [BobcatFeature("…")] title shares nothing with its class name — so the pair is
                // recorded here, where both are in hand, and a monitor can then ask for a
                // specification by identity alone.
                //
                // Bound on a WIDER rule than the steps below, and the difference is the point: a
                // projected specification declares its steps by marker comments, by [BobcatStep]
                // interceptors, or by grammar calls, and only the first of those reaches
                // DeclaredSteps.Register. Keyed on Register's set, a listing would have missed
                // five of Bobcat.Xunit.Samples' seven spec classes while they rendered and
                // published verdicts perfectly well. What makes a test a specification is that
                // [BobcatScenario] records it.
                if (Binds(spec, scenario))
                {
                    var bound = coordinates != null && coordinates.TryGetValue(identity, out var at)
                        ? $", {Quote(at.Slice)}, {quoteOrNull(at.Chapter)}, {quoteOrNull(at.Domain)}, {quoteOrNull(at.Pattern)}"
                        : "";

                    // The method's own binding has no parent; with coordinates it takes the full overload
                    sb.AppendLine(
                        $"            global::Bobcat.DeclaredSteps.Bind({Quote(identity)}, "
                        + $"{Quote(spec.TestClass)}, {Quote(scenario.TestMethod)}{(bound.Length > 0 ? ", null" + bound : "")});");

                    // bobcat#600: the body, and so its steps, belong to a base class's identity.
                    if (scenario.InheritedFrom is { } inheritedFrom)
                    {
                        sb.AppendLine(
                            $"            global::Bobcat.DeclaredSteps.Inherit({Quote(identity)}, {Quote(inheritedFrom)});");
                    }

                    // bobcat#569: each constant row is listed under its own identity, naming the
                    // method's as its parent, with the method's coordinates. A row only a run
                    // produces is not here.
                    foreach (var row in scenario.Rows)
                    {
                        sb.AppendLine(
                            $"            global::Bobcat.DeclaredSteps.Bind({Quote(IdentityOf(spec, scenario, row))}, "
                            + $"{Quote(spec.TestClass)}, {Quote(scenario.TestMethod)}, {Quote(identity)}{bound});");
                    }
                }

                // bobcat#625: what the test says through scenario vocabulary, which only a preview reads
                if (scenario.Vocabulary.Count > 0)
                {
                    var calls = string.Join(", ", scenario.Vocabulary.Select(call =>
                        $"new global::Bobcat.VocabularyStep({Quote(call.Keyword)}, {Quote(call.Text)}, {Quote(call.Grammar)}, {call.Line})"));

                    sb.AppendLine(
                        $"            global::Bobcat.VocabularySteps.Register({Quote(identity)}, {calls});");
                }

                // A test with no marker comments is not a specification. Registering it empty
                // would announce a scenario with nothing to say, which reads as a defect.
                if (scenario.IsUnmarked) continue;

                var steps = string.Join(", ", scenario.Steps.Select(step =>
                    $"new global::Bobcat.DeclaredStep({Quote(step.Keyword)}, {Quote(step.Text)}, {step.Line})"));

                sb.AppendLine(
                    $"            global::Bobcat.DeclaredSteps.Register({Quote(identity)}, {steps});");
            }
        }

        // A listing request writes the manifest here because this is the only code Bobcat owns in
        // a projected suite's process, and a module initializer is the only moment it is sure to
        // run — discovery loads the assembly, so `--list-tests` reaches it without executing a
        // single test. Nothing is written unless BOBCAT_LIST_SPECS was set.
        sb.AppendLine();
        sb.AppendLine("            global::Bobcat.Runtime.SpecManifest.WriteIfRequested(");
        sb.AppendLine("                () => global::Bobcat.DeclaredSteps.Manifest(");
        sb.AppendLine($"                    {Quote(framework)},");
        sb.AppendLine("                    global::System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name");
        sb.AppendLine("                        ?? \"bobcat\",");

        // Issue #469: the recovery hints this assembly declares ride along. Its OWN assembly, not
        // the entry assembly — the attributes are on the classes compiled beside this initializer.
        sb.AppendLine("                    typeof(BobcatDeclaredStepRegistration).Assembly));");

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>True when any scenario declared steps through marker comments.</summary>
    public static bool HasSteps(IEnumerable<MarkedSpec> specs)
        => specs.Any(spec => spec.Scenarios.Any(scenario => !scenario.IsUnmarked));

    /// <summary>
    /// True when there is anything at all to register — the gate on emitting the initializer.
    /// </summary>
    /// <remarks>
    /// Widened by issue #391 from <see cref="HasSteps"/> alone: a suite whose specifications
    /// declare their steps through grammar calls rather than comments has no marker steps to
    /// register, but it still has identities to bind, and without the initializer it could not
    /// answer a listing request at all.
    /// </remarks>
    public static bool HasAnything(IEnumerable<MarkedSpec> specs)
        => specs.Any(spec => spec.Scenarios.Any(scenario =>
            !scenario.IsUnmarked || scenario.Vocabulary.Count > 0 || Binds(spec, scenario)));

    /// <summary>
    /// Whether a scenario's identity is bound and listed: it records, it names a method a filter can
    /// ask for, and its class is one a runner runs — an abstract base's tests run only as a
    /// subclass's, under the subclass's identity (bobcat#600).
    /// </summary>
    internal static bool Binds(MarkedSpec spec, MarkedScenario scenario)
        => scenario.OpensRecording && !spec.IsAbstract && spec.TestClass.Length > 0 && scenario.TestMethod.Length > 0;

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

    private static string quoteOrNull(string? value) => value == null ? "null" : Quote(value);

    /// <summary>
    /// The feature title for a class that carries <c>[BobcatFeature]</c>, or false when it does not
    /// carry one at all — which is how a call site decides whether it sits in a projected test.
    /// </summary>
    internal static bool TryFeatureTitle(
        ClassDeclarationSyntax declaration, SemanticModel model, CancellationToken ct, out string title)
    {
        var attributeTitle = featureAttributeTitle(declaration, model, ct, out var marked);
        title = marked
            ? MarkerSpecNaming.FeatureTitle(declaration.Identifier.ValueText, attributeTitle)
            : "";

        return marked;
    }

    private static string? featureAttributeTitle(
        ClassDeclarationSyntax declaration, SemanticModel model, CancellationToken ct, out bool marked)
    {
        marked = false;

        foreach (var attribute in declaration.AttributeLists.SelectMany(list => list.Attributes))
        {
            if (ShortName(attribute.Name.ToString()) != AttributeName) continue;

            marked = true;

            var argument = attribute.ArgumentList?.Arguments.FirstOrDefault();
            if (argument is null) return null;

            return model.GetConstantValue(argument.Expression, ct).Value as string;
        }

        return null;
    }

    /// <summary>
    /// A test class as a framework's class/method filter spells it: the namespace with dots, then
    /// the type names joined with <c>+</c> for a nested class — xUnit's documented
    /// <c>MyNamespace.MyClass+InnerClass</c>.
    /// </summary>
    internal static string FilterNameOf(INamedTypeSymbol? type)
    {
        if (type is null) return "";

        var names = new List<string>();
        for (var current = type; current is not null; current = current.ContainingType)
        {
            names.Insert(0, current.Name);
        }

        var @namespace = type.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString() + "."
            : "";

        return @namespace + string.Join("+", names);
    }

    /// <summary>
    /// Which test framework owns this compilation's process, by type probe (issue #391) — the same
    /// pattern the <c>EventModelSliceDescriptor</c> gate uses, and for the same reason: "the
    /// assembly is referenced" and "the shape we emit against exists" are different questions.
    /// </summary>
    /// <remarks>
    /// The lane does not settle this. A projected suite is filtered by its framework's own
    /// spelling, so the manifest has to name the framework and not just the lane. An assembly
    /// referencing neither adapter says so with "" rather than guessing, and
    /// <c>SpecFilterArguments</c> then refuses to build a filter for it instead of emitting one
    /// that would silently run everything.
    /// </remarks>
    internal static string FrameworkOf(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName("Bobcat.Xunit.BobcatScenarioAttribute") is not null) return "xunit";
        if (compilation.GetTypeByMetadataName("Bobcat.TUnit.BobcatScenarioAttribute") is not null) return "tunit";
        if (compilation.GetTypeByMetadataName("Bobcat.NUnit.BobcatScenarioAttribute") is not null) return "nunit";
        if (compilation.GetTypeByMetadataName("Bobcat.MSTest.BobcatScenarioAttribute") is not null) return "mstest";
        return "";
    }

    internal static string ShortName(string name)
    {
        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0) name = name.Substring(lastDot + 1);
        return name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name.Substring(0, name.Length - "Attribute".Length)
            : name;
    }
}
