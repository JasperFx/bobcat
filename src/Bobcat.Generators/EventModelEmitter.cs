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
    internal const string Command = "command";
    internal const string Event = "event";
    internal const string Aggregate = "aggregate";
    internal const string ReadModel = "readmodel";
    internal const string Message = "message";

    /// <summary>
    /// The <c>{document}</c> word, and a projected <c>SliceRole.Document</c>: a resolved type with no slot,
    /// so it falls through the stamping switch to the specification's resolved types only.
    /// </summary>
    internal const string Document = "document";

    /// <summary>
    /// Not a capture word: the role an arranged <c>{event}</c> plays (issue #297). It reaches the
    /// descriptor as <c>ConsumedEvents</c> on a View slice and is dropped on a Command slice — see
    /// <c>roleOf</c> and <c>emitSlice</c>.
    /// </summary>
    internal const string Consumed = "consumed";

    /// <summary>
    /// Not a capture word: a projected <c>SliceRole.Context</c> (bobcat#502) — a stream the specification arranges
    /// only so the act can look it up. It reaches the descriptor as <c>ReadsFrom</c>, never as an aggregate.
    /// </summary>
    internal const string Context = "context";

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

        /// <summary>
        /// The pattern the model stated via <c>@pattern:</c> (issue #323), or null when the feature
        /// was hand-written and only inference is available.
        /// </summary>
        public string? DeclaredPattern;
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
        /// <c>Http</c> when a scenario of this slice acts through an HTTP act (issue #258), else
        /// null.
        /// </summary>
        public string? TriggerKind;

        /// <summary>
        /// The route the HTTP act posts to, prefix included, or null when unknowable. "" is unknown too:
        /// <c>adoptRoute</c> lets a known route replace either, whichever scenario folds first.
        /// </summary>
        public string? TriggerRoute;

        /// <summary>
        /// True while <see cref="TriggerRoute"/> is a template rebuilt from an interpolated route (bobcat#500) —
        /// its parameter names are the test's guesses, so a read or resolved route replaces it.
        /// </summary>
        public bool TriggerRouteReconstructed;

        /// <summary>The HTTP act's verb, when its vocabulary declares one; POST otherwise.</summary>
        public string? TriggerHttpMethod;

        /// <summary>
        /// The ASP.NET Core route name the act addressed its endpoint by (bobcat#500), resolved or not. Stamped as
        /// <c>PublisherOrigin.HttpRouteName</c>; it travels with the route it came beside.
        /// </summary>
        public string? TriggerRouteName;

        /// <summary>
        /// True while the slice's trigger came from a read act (bobcat#501). A command act's trigger replaces it,
        /// and it is stamped only when the slice comes out a Query: a GET belongs to the query, not to a command
        /// slice some other specification acts on.
        /// </summary>
        public bool TriggerFromRead;

        /// <summary>
        /// The types the slice's read acts read (bobcat#501). Any entry makes an undeclared slice with no command
        /// act a Query; the types themselves reach <c>ReadsFrom</c> through <see cref="ReadModels"/>.
        /// </summary>
        public readonly List<string> ReadActs = new();
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

        /// <summary>
        /// Every type the slice's specifications arranged as <em>context</em> (bobcat#502): a lookup the act
        /// reads before deciding. Emitted as <c>ReadsFrom</c> (jasperfx#824) whatever the slice's pattern.
        /// </summary>
        public readonly List<string> ReadsFrom = new();
        public readonly List<(string Identity, List<string> Types)> Specifications = new();
        public readonly List<string> PendingSpecifications = new();
    }

    /// <summary>
    /// Fold a projected test's <c>[BobcatSlice]</c> bindings — and, since bobcat#462, the roles its body
    /// states through role-stamped vocabulary — into <paramref name="slices"/> (issue #324).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Roles from the vocabulary.</b>
    /// <c>GivenEvents&lt;Account&gt;(id, new AccountOpened(…))</c>, <c>WhenReceived(new Deposit(…))</c>,
    /// <c>ThenEvents(typeof(FundsDeposited))</c> each state a role, and they land in the
    /// <see cref="SliceModel"/> lists by these rules: every command into <c>Commands</c>, the LAST
    /// one as <c>ActCommand</c>; an arranged event into <c>ArrangedEvents</c>, which only a View slice
    /// emits (#297); a document into resolved types only. <see cref="ProjectedRoles"/> reads the roles; this
    /// only folds.
    /// </para>
    /// <para>
    /// A method-level binding REPLACES the class's, which is how one test class covers several
    /// slices — a rebinding is a whole binding, so a test wanting the class's domain as well says
    /// so. A test with neither a binding nor roles contributes nothing; one with roles and no binding falls
    /// back to the feature title.
    /// </para>
    /// </remarks>
    public static void Collect(MarkerCommentSpecs.MarkedSpec spec, Dictionary<string, SliceModel> slices)
    {
        foreach (var scenario in spec.Scenarios)
        {
            // A method-level binding REPLACES the class's rather than overriding part of it. The
            // first cut concatenated them, so a method rebinding to another slice dragged the
            // class's Domain/Chapter/Pattern along and stamped them on a slice that already had
            // its own answers — CreditWallet came out of the Wallet specs as a Command slice with no
            // chapter and was relabelled an Automation in "Operations". A rebinding is a whole
            // binding: if a test wants the class's domain too, it says so.
            var rebound = scenario.Tags.Count > 0;
            var tags = rebound ? scenario.Tags : spec.Tags;
            var roles = scenario.Roles;

            // No binding and no roles has nothing to say about a model, and
            // inventing a slice for it would fill the canvas with every ordinary test in the suite.
            var declared = GeneratorSliceTags.Slice(tags);
            if (declared == null && roles.Roles.Count == 0) continue;

            var name = declared ?? spec.FeatureTitle;
            if (!slices.TryGetValue(name, out var slice))
            {
                slice = new SliceModel { Name = name, ClassName = SanitizeIdentifier(name) };
                slices[name] = slice;
            }

            slice.Domain ??= GeneratorSliceTags.Domain(tags);
            slice.Chapter ??= GeneratorSliceTags.Chapter(tags);
            slice.DeclaredPattern ??= GeneratorSliceTags.Pattern(tags);

            // bobcat#463, under #258's rule: a method's TriggeredBy wins over its class's whatever order
            // the tests fold in, and the class's is the fallback. A method that rebinds the slice brings
            // its binding whole, so the class's trigger does not follow it onto another slice.
            if (scenario.TriggeredBy is { } methodLabel)
            {
                if (!slice.TriggerLabelDeclaredOnScenario)
                {
                    slice.TriggerLabel = methodLabel;
                    slice.TriggerLabelDeclaredOnScenario = true;
                }
            }
            else if (!rebound)
            {
                slice.TriggerLabel ??= spec.TriggeredBy;
            }

            // The act is the LAST act the test makes (#464); its trigger is the act's own. First
            // scenario to say so wins, like ActCommand.
            slice.ActCommand ??= roles.Act;
            if (roles.ReadAct != null) addDistinct(slice.ReadActs, roles.ReadAct);

            // bobcat#501: a command act outranks a read act's trigger, whichever folds first
            if (roles.Act != null && slice.TriggerFromRead) clearTrigger(slice);

            var fromRead = roles.ReadAct != null;
            if (roles.TriggerKind != null && (!fromRead || slice.ActCommand == null))
            {
                if (slice.TriggerKind == null) slice.TriggerFromRead = fromRead;
                slice.TriggerKind ??= roles.TriggerKind;
                adoptRoute(slice, roles.TriggerRoute, roles.TriggerRouteReconstructed, roles.HttpMethod, roles.TriggerRouteName);
            }

            foreach (var (role, type) in roles.Roles)
            {
                switch (role)
                {
                    case Command: addDistinct(slice.Commands, type); break;
                    case Event: addDistinct(slice.Events, type); break;
                    case Aggregate: addDistinct(slice.Aggregates, type); break;
                    case ReadModel: addDistinct(slice.ReadModels, type); break;
                    case Message: addDistinct(slice.Messages, type); break;
                    case Consumed: addDistinct(slice.ArrangedEvents, type); break;
                    // bobcat#502: a lookup stream is read before deciding, never an aggregate of the slice
                    case Context: addDistinct(slice.ReadsFrom, type); break;
                    // Document reaches the resolved types only, exactly as {document} does
                }
            }

            var identity = MarkerCommentSpecs.IdentityOf(spec, scenario);

            // Issue #404. A pending spec is declared before the behaviour it describes, so it is a
            // hotspot: the design states a question nothing answers yet. The identity goes on the
            // slice, which is what keeps SpecIdentityAudit
            // reading it as JOINED rather than as drift: a pending spec is not an orphan (a test
            // claiming an identity the model lacks) and the slice it names is not a hole (a
            // declared behaviour no test covers). It is one declaration, not yet answered.
            if (scenario.Pending) slice.PendingSpecifications.Add(identity);
            else slice.Specifications.Add((identity, roles.ResolvedTypes()));
        }
    }

    /// <summary>
    /// A known route replaces an unknown one — null or "" — whatever order the scenarios fold in, and brings
    /// its verb with it; a known route is never replaced. One scenario posting to a computed route and
    /// another to "/deposits" put "/deposits" on the slice either way round. bobcat#500 adds a middle rank: a
    /// route <em>reconstructed</em> from an interpolation beats an unknown one and loses to a read or resolved
    /// one, again whatever the order.
    /// </summary>
    /// <remarks>
    /// The route name (bobcat#500) travels with the route it came beside. A route name with no route still names
    /// the endpoint, so it is kept — with its verb — until a scenario brings a known route.
    /// </remarks>
    private static void adoptRoute(SliceModel slice, string? route, bool reconstructed, string? httpMethod, string? routeName)
    {
        if (string.IsNullOrEmpty(route))
        {
            if (string.IsNullOrEmpty(slice.TriggerRoute) && routeName != null && slice.TriggerRouteName == null)
            {
                slice.TriggerRouteName = routeName;
                slice.TriggerHttpMethod ??= httpMethod;
            }

            return;
        }

        if (!string.IsNullOrEmpty(slice.TriggerRoute) && (reconstructed || !slice.TriggerRouteReconstructed)) return;

        slice.TriggerRoute = route;
        slice.TriggerRouteReconstructed = reconstructed;
        slice.TriggerHttpMethod = httpMethod;
        slice.TriggerRouteName = routeName;
    }

    private static void clearTrigger(SliceModel slice)
    {
        slice.TriggerKind = null;
        slice.TriggerRoute = null;
        slice.TriggerRouteReconstructed = false;
        slice.TriggerHttpMethod = null;
        slice.TriggerRouteName = null;
        slice.TriggerFromRead = false;
    }

    /// <summary>A slice name as a C# identifier: letters and digits kept, spaces as underscores.</summary>
    internal static string SanitizeIdentifier(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
            else if (c == ' ')
                sb.Append('_');
        }
        return sb.ToString();
    }

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
        => EmitSource(assemblyName, modelName, slices, Contract.Latest);

    /// <summary>
    /// What the consuming compilation's JasperFx can host beyond the 2.54.0 floor: <c>SlicePattern.Query</c> and
    /// <c>PublisherOrigin.HttpRouteName</c> are 2.85.0's (bobcat#501, #500). Probed, like the gate type, so a
    /// consumer on an older JasperFx keeps compiling: without Query a read act's slice falls back to the inference
    /// it had before; without the field the route name is simply not stamped.
    /// </summary>
    internal sealed class Contract
    {
        public static readonly Contract Latest = new() { Query = true, RouteName = true };

        public bool Query;
        public bool RouteName;
    }

    public static string EmitSource(string assemblyName, string? modelName, IReadOnlyList<SliceModel> slices, Contract contract)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("namespace Bobcat.Generated.EventModel;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Every Event Modeling slice this assembly's specifications (issues #106, #170, #324)");
        sb.AppendLine("/// declare, surfaced through JasperFx's");
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
            sb.Append(emitSlice(slice, contract));
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
    private static string emitSlice(SliceModel slice, Contract contract)
    {
        var pattern = PatternOf(slice, contract);

        // bobcat#501: a Query changes nothing, so it has no command (an arranging command is not its act), and
        // what it reads -- the read act's type, an asserted read model, an explicit GivenContext -- is ReadsFrom,
        // never a read model it produces. ReadsFrom is what JasperFx's ReadModelRead link joins to the View
        // producing it. A stream arranged with plain GivenEvents stamps NOTHING on a Query: it is setup feeding a
        // projection, not something the endpoint reads -- no AggregateTypes, no ReadsFrom, no ConsumedEvents
        var query = pattern == "Query";
        var command = query ? null : slice.ActCommand ?? slice.Commands.FirstOrDefault();
        var readModels = query ? EmptyTypes : slice.ReadModels;
        var aggregates = query ? EmptyTypes : slice.Aggregates;
        var readsFrom = slice.ReadsFrom;
        if (query)
        {
            readsFrom = new List<string>(slice.ReadsFrom);
            foreach (var type in slice.ReadModels) addDistinct(readsFrom, type);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"    /// <summary>The <c>{escapeXml(slice.Name)}</c> slice.</summary>");
        sb.AppendLine($"    internal static global::{Ns}.EventModelSliceDescriptor {slice.ClassName}()");
        sb.AppendLine($"        => new global::{Ns}.EventModelSliceDescriptor(");
        sb.AppendLine($"            {literal(slice.Name)},");
        sb.AppendLine($"            {literal(slice.TriggerLabel)},");
        sb.AppendLine("            null,");
        sb.AppendLine($"            {typeDescriptorOrNull(command)},");
        sb.AppendLine("            null,");
        sb.AppendLine($"            {typeDescriptorList(slice.Events)},");
        sb.AppendLine($"            {typeDescriptorList(EmptyTypes)},");
        sb.AppendLine($"            {typeDescriptorList(readModels)})");
        sb.AppendLine("        {");
        if (slice.Domain != null) sb.AppendLine($"            Domain = {literal(slice.Domain)},");
        if (slice.Chapter != null) sb.AppendLine($"            Chapter = {literal(slice.Chapter)},");
        if (pattern != null) sb.AppendLine($"            Pattern = global::{Ns}.SlicePattern.{pattern},");
        // bobcat#501: a read act's trigger is the query's; on a slice that is not a Query it is left off
        if (slice.TriggerKind != null && (!slice.TriggerFromRead || query))
        {
            sb.AppendLine($"            TriggerKind = global::{Ns}.TriggerKind.{slice.TriggerKind},");
            // POST is the default verb; a projected act's vocabulary may name another through
            // [EventModelTrigger(HttpMethod = …)].
            var verb = slice.TriggerHttpMethod ?? "POST";
            if (!string.IsNullOrEmpty(slice.TriggerRoute))
            {
                sb.AppendLine($"            TriggerOrigin = new global::{Ns}.PublisherOrigin");
                sb.AppendLine("            {");
                sb.AppendLine($"                HttpRoute = {literal(slice.TriggerRoute)},");
                sb.AppendLine($"                HttpMethod = {literal(verb)},");
                if (slice.TriggerRouteName != null && contract.RouteName)
                    sb.AppendLine($"                HttpRouteName = {literal(slice.TriggerRouteName)},");
                sb.AppendLine($"                Label = {literal(verb + " " + slice.TriggerRoute)}");
                sb.AppendLine("            },");
            }
            else if (slice.TriggerRouteName != null && slice.TriggerKind == "Http" && contract.RouteName)
            {
                // bobcat#500: a route name that resolved to no template (a minimal API's .WithName, a typo) still
                // names the endpoint. The label says it is a name, so it cannot be read as a route
                sb.AppendLine($"            TriggerOrigin = new global::{Ns}.PublisherOrigin");
                sb.AppendLine("            {");
                sb.AppendLine($"                HttpMethod = {literal(verb)},");
                sb.AppendLine($"                HttpRouteName = {literal(slice.TriggerRouteName)},");
                sb.AppendLine($"                Label = {literal($"{verb} (route name {slice.TriggerRouteName})")}");
                sb.AppendLine("            },");
            }
        }
        sb.AppendLine($"            AggregateTypes = {typeDescriptorList(aggregates)},");
        // Issue #297: only a View slice consumes what its scenarios arranged. On a Command slice
        // the same Givens are the aggregate's own stream, and stamping them would draw an arrow
        // from every slice that emits WalletOpened into every slice that merely starts from it.
        if (pattern == "View" && slice.ArrangedEvents.Count > 0)
            sb.AppendLine($"            ConsumedEvents = {typeDescriptorList(slice.ArrangedEvents)},");
        // bobcat#502: a context stream is what the slice reads before deciding. Written only when there is
        // one, like ConsumedEvents, so a model with no context keeps the descriptor it always had
        if (readsFrom.Count > 0)
            sb.AppendLine($"            ReadsFrom = {typeDescriptorList(readsFrom)},");
        sb.AppendLine($"            PublishedMessages = {typeDescriptorList(slice.Messages)},");
        sb.AppendLine($"            Specifications = {specifications(slice)},");
        sb.AppendLine($"            Hotspots = {hotspots(slice)}");
        sb.AppendLine("        };");
        return sb.ToString();
    }

    private static readonly List<string> EmptyTypes = new();

    /// <summary>
    /// Each specification's Event Model coordinates (bobcat#573): the slice it folds into, that slice's chapter,
    /// domain and pattern — keyed by identity. Folded by <see cref="Collect"/> and <see cref="PatternOf"/>, the
    /// same calls the descriptor is emitted from, so the manifest and the platform traits cannot say something
    /// the Event Model does not. Pending specifications have coordinates too.
    /// </summary>
    internal static Dictionary<string, (string Slice, string? Chapter, string? Domain, string? Pattern)> Coordinates(
        IEnumerable<MarkerCommentSpecs.MarkedSpec> specs, Contract contract)
    {
        var slices = new Dictionary<string, SliceModel>();
        foreach (var spec in specs) Collect(spec, slices);

        var coordinates = new Dictionary<string, (string, string?, string?, string?)>(StringComparer.Ordinal);
        foreach (var slice in slices.Values)
        {
            var value = (slice.Name, slice.Chapter, slice.Domain, PatternOf(slice, contract));
            foreach (var specification in slice.Specifications)
            {
                if (!coordinates.ContainsKey(specification.Identity)) coordinates[specification.Identity] = value;
            }

            foreach (var identity in slice.PendingSpecifications)
            {
                if (!coordinates.ContainsKey(identity)) coordinates[identity] = value;
            }
        }

        return coordinates;
    }

    /// <summary>
    /// Which pattern this is. A declared <c>Pattern</c> settles it outright, which is how a scaffolded
    /// specification carries the curated model's answer through.
    ///
    /// Failing that, inference: a slice whose act sends a command is a Command slice; one whose act is a
    /// read-only HTTP call (GET/HEAD with a read role, bobcat#501) and none a command is a Query; one with
    /// only arranging commands is still Command; one that only asserts on a read model is a View.
    /// Automation and Translation need a trigger the test does not express, so they stay null rather
    /// than being guessed — a wrong pattern miscolours the canvas, a null one does not.
    /// </summary>
    internal static string? PatternOf(SliceModel slice, Contract contract)
    {
        // The model's own word, when it gave one (issue #323). Inference below cannot reach
        // Automation or Translation, and guessing Command for an automation produced a
        // SourceDisagreement against the curated file that no change to either could resolve.
        if (slice.DeclaredPattern is { } declared) return declared;

        if (slice.ActCommand != null) return "Command";
        if (slice.ReadActs.Count > 0 && contract.Query) return "Query";
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
