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

    /// <summary>One slice of the model, accumulated across every scenario that declares it.</summary>
    internal sealed class SliceModel
    {
        public string Name = "";
        public string? Domain;
        public string? TriggerLabel;
        public string ClassName = "";
        public readonly List<string> Commands = new();

        /// <summary>
        /// The command the slice is <em>about</em>, as opposed to every command its scenarios
        /// happen to name. See <c>actCommandOf</c>.
        /// </summary>
        public string? ActCommand;
        public readonly List<string> Events = new();
        public readonly List<string> Aggregates = new();
        public readonly List<string> ReadModels = new();
        public readonly List<string> Messages = new();
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
        FeatureInfo feature, List<MatchedScenario> matched, Dictionary<string, SliceModel> slices)
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
            slice.TriggerLabel ??= triggerLabel;
            slice.ActCommand ??= actCommandOf(scenario);

            foreach (var (role, type) in roles)
            {
                switch (role)
                {
                    case Command: addDistinct(slice.Commands, type); break;
                    case Event: addDistinct(slice.Events, type); break;
                    case Aggregate: addDistinct(slice.Aggregates, type); break;
                    case ReadModel: addDistinct(slice.ReadModels, type); break;
                    case Message: addDistinct(slice.Messages, type); break;
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
            yield return (parameter.ParameterName, match.ExtractedValues[i]);
        }
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
        var pattern = patternOf(slice);
        if (pattern != null) sb.AppendLine($"            Pattern = global::{Ns}.SlicePattern.{pattern},");
        sb.AppendLine($"            AggregateTypes = {typeDescriptorList(slice.Aggregates)},");
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
