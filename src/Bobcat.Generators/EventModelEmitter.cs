using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Bobcat.Generators;

/// <summary>
/// Emits a JasperFx <c>EventModelSliceDescriptor</c> per feature, plus one assembly-wide
/// <c>IEventModelDefinitionSource</c> that surfaces them — issue #106, the first real
/// implementation of that interface anywhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Roles, not a graph.</b> <c>EventModelSliceDescriptor.Elements</c> and <c>.Edges</c> are
/// <em>computed</em> upstream from the typed roles on every read, so this emitter only ever
/// stamps roles — command, events, aggregates, read models, messages. Building the element graph
/// here would produce a second opinion about the same slice, which is exactly what the upstream
/// "computed on read" design exists to prevent.
/// </para>
/// <para>
/// <b>Gated on the reference.</b> Nothing is emitted unless the consuming compilation actually
/// references JasperFx.Events — most Bobcat suites do not do event sourcing, and generating code
/// against a package they never asked for would break their build. The probe is a type lookup
/// rather than an assembly-name check on purpose: JasperFx.Events 2.53.0 shipped an early,
/// incompatible sketch of this namespace, so "the assembly is present" is not the same question
/// as "the shape I emit against is present".
/// </para>
/// <para>
/// <b>No reflection, same as everything else here.</b> A role's type reaches the descriptor as
/// <c>TypeDescriptor.For(typeof(global::Some.Type))</c>, reusing the very <c>typeof</c> the step
/// binding already emits, so a renamed command breaks the build at the feature that names it —
/// which is #106's acceptance criterion.
/// </para>
/// </remarks>
internal static class EventModelEmitter
{
    /// <summary>The type whose presence means the consuming compilation can host a descriptor.</summary>
    public const string GateTypeName = "JasperFx.Events.EventModeling.EventModelSliceDescriptor";

    private const string Ns = "JasperFx.Events.EventModeling";
    private const string TypeDesc = "global::JasperFx.Descriptors.TypeDescriptor";

    /// <summary>The Event Modeling role words, in the order their slots appear on the descriptor.</summary>
    private const string Command = "command";
    private const string Event = "event";
    private const string Aggregate = "aggregate";
    private const string ReadModel = "readmodel";
    private const string Message = "message";

    /// <summary>
    /// Not a capture word: the role an arranged <c>{event}</c> plays (issue #297). It reaches the
    /// descriptor as <c>ConsumedEvents</c> on a View slice and is dropped on a Command slice — see
    /// <c>roleOf</c> and <c>emitSlice</c>.
    /// </summary>
    private const string Consumed = "consumed";

    /// <summary>One slice of the model, accumulated across every scenario that declares it.</summary>
    internal sealed class SliceModel
    {
        public string Name = "";
        public string? Domain;

        /// <summary>
        /// The <c>@chapter:</c> tag (issue #298) — the span of the timeline the slice sits in, the
        /// grouping every Event Modeling tool zooms into. Orthogonal to <see cref="Domain"/>.
        /// </summary>
        public string? Chapter;
        public string? TriggerLabel;
        public string ClassName = "";
        public readonly List<string> Commands = new();

        /// <summary>
        /// The command the slice is <em>about</em>, as opposed to every command its scenarios
        /// happen to name. See <c>actCommandOf</c>.
        /// </summary>
        public string? ActCommand;

        /// <summary>
        /// True once a scenario's own <c>Triggered by</c> line set <see cref="TriggerLabel"/>, so a
        /// feature-level fallback from a scenario folded later cannot replace it (issue #258).
        /// </summary>
        public bool TriggerLabelDeclaredOnScenario;

        /// <summary>
        /// <c>Http</c> when a scenario of this slice acts through the HTTP grammar (issue #258),
        /// else null. The one trigger kind Gherkin can settle on its own — see <c>httpActOf</c>.
        /// </summary>
        public string? TriggerKind;

        /// <summary>The route the HTTP act posts to, prefix included, or null when unknowable.</summary>
        public string? TriggerRoute;
        public readonly List<string> Events = new();
        public readonly List<string> Aggregates = new();
        public readonly List<string> ReadModels = new();
        public readonly List<string> Messages = new();

        /// <summary>
        /// Every <c>{event}</c> the slice's scenarios <em>arranged</em> (issue #297). Emitted as
        /// <c>ConsumedEvents</c> only when the slice comes out a View — on a Command slice arranged
        /// history is the aggregate's stream, and the list is discarded at emit time.
        /// </summary>
        public readonly List<string> ArrangedEvents = new();
        public readonly List<(string Identity, List<string> Types)> Specifications = new();
        public readonly List<string> PendingSpecifications = new();
    }

    /// <summary>
    /// Fold a matched feature's scenarios into <paramref name="slices"/>, keyed by slice name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The slice is a scenario-level grouping, not a feature-level one.</b> A feature is a
    /// document; a slice is a vertical behaviour, and several specs usually describe the same one
    /// — <c>Wallet.feature</c> declares <c>@slice:OpenWallet</c> once and <c>@slice:CreditWallet</c>
    /// three times. Because <c>SimpleGherkinParser</c> already merges feature tags into every
    /// scenario's tag list, reading the tag off the scenario handles both placements with one rule,
    /// and a slice may legitimately span several feature files.
    /// </para>
    /// <para>
    /// Slices accumulate across the whole compilation rather than per feature, so two features
    /// contributing to one slice produce one descriptor rather than two that a consumer has to
    /// merge. Upstream's <c>EventModelSliceDescriptor.Merge</c> still folds across <em>sources</em>;
    /// this just avoids handing it work we already have the facts to do.
    /// </para>
    /// </remarks>
    public static void Collect(
        FeatureInfo feature, List<MatchedScenario> matched, Dictionary<string, SliceModel> slices,
        FixtureInfo? fixture = null)
    {
        var triggerLabel = GeneratorSliceTags.TriggeredBy(feature.Description);

        foreach (var scenario in matched)
        {
            var tags = scenario.Scenario.Tags;
            var declared = GeneratorSliceTags.Slice(tags);
            var identity = $"{feature.Title}/{scenario.Scenario.Title}";

            var resolved = new List<string>();
            var roles = new List<(string Role, string Type)>();
            foreach (var step in scenario.Steps)
            {
                foreach (var role in rolesOf(step))
                {
                    roles.Add(role);
                    addDistinct(resolved, role.Type);
                }
            }

            // An untagged scenario with no Event Modeling roles has nothing to say about a model,
            // and inventing a slice for it would fill the canvas with every ordinary test in the
            // suite. An untagged scenario that DOES resolve roles still counts — it falls back to
            // the feature title, which is the best name available.
            if (declared == null && roles.Count == 0) continue;

            var name = declared ?? feature.Title;
            if (!slices.TryGetValue(name, out var slice))
            {
                slice = new SliceModel { Name = name, ClassName = CodeEmitter.SanitizeIdentifier(name) };
                slices[name] = slice;
            }

            slice.Domain ??= GeneratorSliceTags.Domain(tags);
            slice.Chapter ??= GeneratorSliceTags.Chapter(tags);
            // A slice is scenario-level, and so is its trigger (issue #258). A feature-level
            // "Triggered by" used to be stamped on every slice the feature held, which put nine
            // wrong labels on the CritterCrush canvas from one line. A scenario's own line wins,
            // whichever order the scenarios arrive in; the feature's stays the fallback, and is
            // right when the feature's slices share a trigger (Wallet's all start with the holder).
            if (GeneratorSliceTags.TriggeredBy(scenario.Scenario.Description) is { } declaredLabel)
            {
                if (!slice.TriggerLabelDeclaredOnScenario)
                {
                    slice.TriggerLabel = declaredLabel;
                    slice.TriggerLabelDeclaredOnScenario = true;
                }
            }
            else
            {
                slice.TriggerLabel ??= triggerLabel;
            }
            slice.ActCommand ??= actCommandOf(scenario);

            // Issue #258, gap 4. `is posted to` is the HTTP grammar's own sentence, so a scenario
            // that uses it IS reached over HTTP — a compile-time fact rather than a guess, which is
            // the bar every other role on this descriptor is held to. First scenario to say so
            // wins, like ActCommand: a slice reached over HTTP does not stop being so because a
            // later scenario of the same slice drives it another way.
            if (httpActOf(scenario, fixture) is { } act)
            {
                slice.TriggerKind ??= "Http";
                slice.TriggerRoute ??= act;
            }

            foreach (var (role, type) in roles)
            {
                switch (role)
                {
                    case Command: addDistinct(slice.Commands, type); break;
                    case Event: addDistinct(slice.Events, type); break;
                    case Aggregate: addDistinct(slice.Aggregates, type); break;
                    case ReadModel: addDistinct(slice.ReadModels, type); break;
                    case Message: addDistinct(slice.Messages, type); break;
                    case Consumed: addDistinct(slice.ArrangedEvents, type); break;
                    // {type} is the general form and carries no Event Modeling role, so it reaches
                    // the specification's resolved types but never a slice slot.
                }
            }

            // A scenario with no steps is declared but unbound — the spec-driven form of an open
            // question, which is what jasperfx#689 means by a pending-specification hotspot. A
            // scenario whose steps did not MATCH is not this case: that is already a compile error
            // and the whole feature is skipped before it reaches here.
            if (scenario.Steps.Count == 0) slice.PendingSpecifications.Add(identity);
            else slice.Specifications.Add((identity, resolved));
        }
    }

    /// <summary>
    /// Fold a code-first specification's scenarios into <paramref name="slices"/> (issue #170) —
    /// the same rules as the Gherkin overload, applied to what <see cref="CodeFirstSpecs"/>
    /// extracted: the slice is a scenario-level grouping, an untagged scenario with no roles
    /// contributes nothing, the identity is <c>{FeatureTitle}/{ScenarioTitle}</c>, and an empty
    /// scenario method is a pending-specification hotspot. The one asymmetry is the trigger
    /// label: code-first has no <c>Triggered by</c> line, so none is stamped.
    /// </summary>
    public static void Collect(CodeFirstSpecs.SpecInfo spec, Dictionary<string, SliceModel> slices)
    {
        foreach (var scenario in spec.Scenarios)
        {
            var declared = GeneratorSliceTags.Slice(scenario.Tags);
            if (declared == null && scenario.Roles.Count == 0) continue;

            var name = declared ?? spec.FeatureTitle;
            if (!slices.TryGetValue(name, out var slice))
            {
                slice = new SliceModel { Name = name, ClassName = CodeEmitter.SanitizeIdentifier(name) };
                slices[name] = slice;
            }

            slice.Domain ??= GeneratorSliceTags.Domain(scenario.Tags);
            slice.Chapter ??= GeneratorSliceTags.Chapter(scenario.Tags);
            slice.ActCommand ??= scenario.ActCommand;

            var resolved = new List<string>();
            foreach (var (role, type) in scenario.Roles)
            {
                addDistinct(resolved, type);
                switch (role)
                {
                    case Command: addDistinct(slice.Commands, type); break;
                    case Event: addDistinct(slice.Events, type); break;
                    case Aggregate: addDistinct(slice.Aggregates, type); break;
                    case ReadModel: addDistinct(slice.ReadModels, type); break;
                    case Message: addDistinct(slice.Messages, type); break;
                    case Consumed: addDistinct(slice.ArrangedEvents, type); break;
                }
            }

            var identity = $"{spec.FeatureTitle}/{scenario.Title}";
            if (scenario.IsPending) slice.PendingSpecifications.Add(identity);
            else slice.Specifications.Add((identity, resolved));
        }
    }

    /// <summary>
    /// The command this scenario is actually specifying: the <em>last</em> <c>{command}</c>
    /// captured on a <c>When</c> step.
    /// </summary>
    /// <remarks>
    /// Not the first command the scenario names. A spec commonly arranges state by issuing
    /// earlier commands — <c>Wallet.feature</c>'s CreditWallet scenarios open the wallet with a
    /// <c>When OpenWallet is received</c> before the <c>When CreditWallet is received</c> they are
    /// about — so "first command wins" labelled the CreditWallet slice with OpenWallet. Given /
    /// When / Then ordering makes the final <c>When</c> the act; everything before it is arrange.
    /// The arrange commands are not lost: they still reach the specification's resolved types,
    /// which is where evidence of "this spec touched that type" belongs.
    /// </remarks>
    private static string? actCommandOf(MatchedScenario scenario)
    {
        string? act = null;
        foreach (var step in scenario.Steps)
        {
            if (!string.Equals(step.Step.ResolvedKeyword.Trim(), "When", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var (role, type) in rolesOf(step))
                if (role == Command) act = type;
        }

        return act;
    }

    /// <summary>
    /// The HTTP grammar's step sentence. Matched on the <em>expression</em> — what the grammar
    /// declared in its attribute — rather than on a type name, so the generator keeps needing no
    /// reference to the package that ships it, the same rule the marker-comment and interceptor
    /// pipelines follow.
    /// </summary>
    private const string HttpActExpression = "is posted to";

    /// <summary>The constructor parameter a grammar module prefixes its routes with.</summary>
    private const string RoutePrefixParameter = "routePrefix";

    /// <summary>
    /// The route this scenario's HTTP act posts to, or null when it has no HTTP act.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <em>last</em> <c>When</c>, for the same reason <c>actCommandOf</c> takes the last one:
    /// earlier Whens arrange, the final one is the act. Returns the route with the module's
    /// <c>routePrefix</c> applied, because that is the route the app actually serves.
    /// </para>
    /// <para>
    /// <b>An empty string means "HTTP, route unknown".</b> A module whose prefix is resolved from
    /// the scenario rather than from an <c>[IncludeGrammars]</c> literal has no compile-time
    /// route, and a route missing its prefix is a wrong route — worse on a canvas than no route at
    /// all. The <em>kind</em> is still certain, so it is still stamped.
    /// </para>
    /// <para>
    /// Only Http, and only from this sentence. <c>When {command} is received</c> dispatches
    /// ordinary commands as readily as it does messages a handler is subscribed to, so deriving
    /// <c>MessageHandler</c> from it would be the guess this method exists to avoid (issue #258
    /// gap 4, classified "not Gherkin's job").
    /// </para>
    /// </remarks>
    private static string? httpActOf(MatchedScenario scenario, FixtureInfo? fixture)
    {
        string? route = null;

        foreach (var step in scenario.Steps)
        {
            if (!string.Equals(step.Step.ResolvedKeyword.Trim(), "When", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = step.Match;
            var method = match?.Method;
            if (match == null || method == null) continue;
            if (method.Expression.IndexOf(HttpActExpression, StringComparison.Ordinal) < 0) continue;

            route = routePrefixOf(method, fixture) + stringCaptureOf(match, method);
        }

        return route;
    }

    /// <summary>The first <c>{string}</c> the step captured — the route on the HTTP act.</summary>
    private static string stringCaptureOf(StepMatcher.MatchResult match, StepMethodInfo method)
    {
        var parsed = method.ParsedExpression;
        if (parsed == null) return "";

        for (var i = 0; i < parsed.Parameters.Count && i < match.ExtractedValues.Count; i++)
        {
            // The {string} capture group excludes its quotes, so this is the route as written.
            if (parsed.Parameters[i].CSharpType == "string") return match.ExtractedValues[i];
        }

        return "";
    }

    /// <summary>
    /// The literal route prefix the declaring grammar module was composed with, or "" when there
    /// is none to read.
    /// </summary>
    private static string routePrefixOf(StepMethodInfo method, FixtureInfo? fixture)
    {
        if (fixture == null || method.DeclaringModule == null) return "";

        foreach (var module in fixture.Modules)
        {
            if (module.FullyQualifiedName != method.DeclaringModule) continue;

            foreach (var argument in module.ConstructionParameters)
            {
                if (argument.Parameter.Name != RoutePrefixParameter) continue;

                // A prefix resolved from the scenario has no literal, and guessing "" for it would
                // publish a route missing its prefix.
                return argument.Literal == null ? "" : unquote(argument.Literal);
            }

            return "";
        }

        return "";
    }

    private static string unquote(string literal)
        => literal.Length >= 2 && literal[0] == '"' && literal[literal.Length - 1] == '"'
            ? literal.Substring(1, literal.Length - 2)
            : literal;

    /// <summary>
    /// A role word carried by no slot on the descriptor. The type is still a type the
    /// specification resolved — it belongs in <c>ResolvedTypes</c>, which is what run evidence
    /// joins on — but it describes no element of the slice, so the stamping switch has no case for
    /// it and it falls through.
    /// </summary>
    /// <summary>Every (role word, qualified type) a matched step resolved.</summary>
    private static IEnumerable<(string Role, string Type)> rolesOf(MatchedStep step)
    {
        var match = step.Match;
        var parsed = match?.Method.ParsedExpression;
        if (match == null || parsed == null) yield break;

        for (var i = 0; i < parsed.Parameters.Count && i < match.ExtractedValues.Count; i++)
        {
            var parameter = parsed.Parameters[i];
            if (parameter.CSharpType != CucumberExpressionParser.TypeCSharpType) continue;
            if (parameter.ParameterName == null) continue;

            // resolveTypeCaptures has already overwritten the raw Gherkin word with the
            // global::-qualified name, so an unresolved capture never reaches here — it failed the
            // build as BOBCAT011/BOBCAT012 first.
            yield return (roleOf(parameter.ParameterName, step), match.ExtractedValues[i]);
        }
    }

    /// <summary>
    /// The role a capture actually plays, which the capture word alone does not always settle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An <c>{event}</c> on a <c>Given</c> is arranged history, not an emitted event</b> (issue
    /// #259). A slice's <c>EmittedEvents</c> are what it <em>produces</em>; the events a scenario
    /// lays down first are prior facts it runs against, and stamping them would put
    /// <c>WalletOpened</c> on the CreditWallet slice as an output it never writes — a wrong arrow
    /// on the canvas, which is worse than a missing one.
    /// </para>
    /// <para>
    /// <b>But in a View slice, arranged history is what the slice <em>consumes</em></b> (issue
    /// #297, canvas design decision 3). <c>Given AccountOpened occurred … Then the AccountBalance
    /// read model contains</c> is the best evidence there is that the projection applies
    /// AccountOpened, and <em>consumed</em> is a different claim from <em>emitted</em> — upstream
    /// gives it its own role, <c>ConsumedEvents</c>, which becomes the State View arrow. So the
    /// arranged event is collected under <see cref="Consumed"/> here and the decision is made per
    /// slice at emit time: a View slice emits the list as <c>ConsumedEvents</c>, a Command slice
    /// drops it (there the history is the aggregate's stream, and the #259 demotion stands).
    /// </para>
    /// <para>
    /// The rule is not new; it is newly <em>expressible</em>. <c>Given events for {aggregate}</c>
    /// names its event types in a table cell, so they were never captures and never reached this
    /// method — the exemption came free from the shape. The per-event <c>Given {event} occurred</c>
    /// names the type in the step text, which is the whole point of it, so the exemption has to be
    /// stated rather than inherited from a limitation.
    /// </para>
    /// <para>
    /// The type is still <em>resolved</em>: it stays in the specification's <c>ResolvedTypes</c>,
    /// which is where "this spec touched that type" belongs and what issue #107's run evidence
    /// joins on — exactly the treatment arrange <em>commands</em> already get.
    /// </para>
    /// </remarks>
    private static string roleOf(string parameterName, MatchedStep step)
        => parameterName == Event && isArrange(step) ? Consumed : parameterName;

    private static bool isArrange(MatchedStep step)
        => string.Equals(step.Step.ResolvedKeyword, "Given", StringComparison.OrdinalIgnoreCase);

    private static void addDistinct(List<string> list, string value)
    {
        if (!list.Contains(value)) list.Add(value);
    }

    /// <summary>
    /// Emit the assembly's Event Model: one builder per slice plus the
    /// <c>IEventModelDefinitionSource</c> that surfaces them, and the DI registration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One file for the whole assembly, because a slice may be contributed by several features
    /// and a per-feature file could not hold it without one of them winning arbitrarily.
    /// </para>
    /// <para>
    /// <paramref name="modelName"/> is the assembly-level <c>[EventModelName]</c> override, or null
    /// for the assembly-name default. It renames only the <em>descriptor</em> — the merge key
    /// upstream — while <c>Subject</c> keeps the assembly name, because the subject identifies the
    /// source and two spec assemblies may legitimately feed one model (issue #172).
    /// </para>
    /// </remarks>
    public static string EmitSource(string assemblyName, string? modelName, IReadOnlyList<SliceModel> slices)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace Bobcat.Generated.EventModel;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Every Event Modeling slice this assembly's <c>.feature</c> files (issue #106) and");
        sb.AppendLine("/// code-first specifications (issue #170) declare, surfaced through JasperFx's");
        sb.AppendLine("/// <c>IEventModelDefinitionSource</c>. Register with <c>services.AddBobcatEventModel()</c>.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine($"internal sealed class BobcatEventModelSource : global::{Ns}.IEventModelDefinitionSource");
        sb.AppendLine("{");
        sb.AppendLine("    internal static readonly BobcatEventModelSource Instance = new();");
        sb.AppendLine();
        sb.AppendLine($"    public global::System.Uri Subject {{ get; }} = new global::System.Uri({literal("event-model://" + assemblyName)});");
        sb.AppendLine();
        sb.AppendLine($"    public global::System.Threading.Tasks.Task<global::{Ns}.EventModelDescriptor?> TryCreateAsync(");
        sb.AppendLine("        global::System.IServiceProvider services, global::System.Threading.CancellationToken token)");
        sb.AppendLine($"        => global::System.Threading.Tasks.Task.FromResult<global::{Ns}.EventModelDescriptor?>(Describe());");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>The descriptor. No service provider needed — it is all compile-time fact.</summary>");
        sb.AppendLine($"    internal static global::{Ns}.EventModelDescriptor Describe()");
        sb.AppendLine($"        => new global::{Ns}.EventModelDescriptor(");
        sb.AppendLine($"            {literal(modelName ?? assemblyName)},");
        sb.AppendLine($"            new global::{Ns}.EventModelSliceDescriptor[]");
        sb.AppendLine("            {");
        foreach (var slice in slices) sb.AppendLine($"                {slice.ClassName}(),");
        sb.AppendLine("            });");

        foreach (var slice in slices)
        {
            sb.AppendLine();
            sb.Append(emitSlice(slice));
        }

        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Registers the generated source for JasperFx's Event Model discovery.</summary>");
        sb.AppendLine("internal static class BobcatEventModelRegistration");
        sb.AppendLine("{");
        sb.AppendLine("    internal static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddBobcatEventModel(");
        sb.AppendLine("        this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
        sb.AppendLine($"        => global::{Ns}.EventModelServiceCollectionExtensions.AddEventModelSource(services, BobcatEventModelSource.Instance);");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Emit one slice's builder as a member of the model class.</summary>
    private static string emitSlice(SliceModel slice)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"    /// <summary>The <c>{escapeXml(slice.Name)}</c> slice.</summary>");
        sb.AppendLine($"    internal static global::{Ns}.EventModelSliceDescriptor {slice.ClassName}()");
        sb.AppendLine($"        => new global::{Ns}.EventModelSliceDescriptor(");
        sb.AppendLine($"            {literal(slice.Name)},");
        sb.AppendLine($"            {literal(slice.TriggerLabel)},");
        sb.AppendLine("            null,");
        sb.AppendLine($"            {typeDescriptorOrNull(slice.ActCommand ?? slice.Commands.FirstOrDefault())},");
        sb.AppendLine("            null,");
        sb.AppendLine($"            {typeDescriptorList(slice.Events)},");
        sb.AppendLine($"            {typeDescriptorList(EmptyTypes)},");
        sb.AppendLine($"            {typeDescriptorList(slice.ReadModels)})");
        sb.AppendLine("        {");
        if (slice.Domain != null) sb.AppendLine($"            Domain = {literal(slice.Domain)},");
        if (slice.Chapter != null) sb.AppendLine($"            Chapter = {literal(slice.Chapter)},");
        var pattern = patternOf(slice);
        if (pattern != null) sb.AppendLine($"            Pattern = global::{Ns}.SlicePattern.{pattern},");
        if (slice.TriggerKind != null)
        {
            sb.AppendLine($"            TriggerKind = global::{Ns}.TriggerKind.{slice.TriggerKind},");
            if (!string.IsNullOrEmpty(slice.TriggerRoute))
            {
                // POST is the only verb the HTTP grammar has; when it grows others the verb comes
                // from the step's own sentence rather than from a default here.
                sb.AppendLine($"            TriggerOrigin = new global::{Ns}.PublisherOrigin");
                sb.AppendLine("            {");
                sb.AppendLine($"                HttpRoute = {literal(slice.TriggerRoute)},");
                sb.AppendLine("                HttpMethod = \"POST\",");
                sb.AppendLine($"                Label = {literal("POST " + slice.TriggerRoute)}");
                sb.AppendLine("            },");
            }
        }
        sb.AppendLine($"            AggregateTypes = {typeDescriptorList(slice.Aggregates)},");
        // Issue #297: only a View slice consumes what its scenarios arranged. On a Command slice
        // the same Givens are the aggregate's own stream, and stamping them would draw an arrow
        // from every slice that emits WalletOpened into every slice that merely starts from it.
        if (pattern == "View" && slice.ArrangedEvents.Count > 0)
            sb.AppendLine($"            ConsumedEvents = {typeDescriptorList(slice.ArrangedEvents)},");
        sb.AppendLine($"            PublishedMessages = {typeDescriptorList(slice.Messages)},");
        sb.AppendLine($"            Specifications = {specifications(slice)},");
        sb.AppendLine($"            Hotspots = {hotspots(slice)}");
        sb.AppendLine("        };");
        return sb.ToString();
    }

    private static readonly List<string> EmptyTypes = new();

    /// <summary>
    /// Which of the four canonical patterns this is, when Gherkin alone can tell. A slice that
    /// receives a command is a Command slice; one that only asserts on a read model is a View.
    /// Automation and Translation need a trigger Gherkin does not express, so they stay null
    /// rather than being guessed — a wrong pattern miscolours the canvas, a null one does not.
    /// </summary>
    private static string? patternOf(SliceModel slice)
    {
        if (slice.Commands.Count > 0) return "Command";
        if (slice.ReadModels.Count > 0) return "View";
        return null;
    }

    private static string specifications(SliceModel slice)
    {
        if (slice.Specifications.Count == 0)
            return $"global::System.Array.Empty<global::{Ns}.SpecificationDescriptor>()";

        var items = slice.Specifications.Select(spec =>
            $"new global::{Ns}.SpecificationDescriptor({literal(spec.Identity)}, {typeDescriptorList(spec.Types)})");
        return $"new global::{Ns}.SpecificationDescriptor[] {{ {string.Join(", ", items)} }}";
    }

    private static string hotspots(SliceModel slice)
    {
        if (slice.PendingSpecifications.Count == 0)
            return $"global::System.Array.Empty<global::{Ns}.HotspotDescriptor>()";

        var items = slice.PendingSpecifications.Select(id =>
            $"global::{Ns}.HotspotDescriptor.PendingSpecification({literal(id)})");
        return $"new global::{Ns}.HotspotDescriptor[] {{ {string.Join(", ", items)} }}";
    }

    private static string typeDescriptorOrNull(string? qualified)
        => qualified == null ? "null" : $"{TypeDesc}.For(typeof({qualified}))";

    private static string typeDescriptorList(List<string> qualified)
    {
        if (qualified.Count == 0) return $"global::System.Array.Empty<{TypeDesc}>()";
        var items = qualified.Select(q => $"{TypeDesc}.For(typeof({q}))");
        return $"new {TypeDesc}[] {{ {string.Join(", ", items)} }}";
    }

    private static string literal(string? value)
        => value == null
            ? "null"
            : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    private static string escapeXml(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
