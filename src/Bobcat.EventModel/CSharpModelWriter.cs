using System.Text;
using JasperFx.CodeGeneration;

namespace Bobcat.EventModel;

/// <summary>
/// Writes an imported event model out as <b>C#</b> (issue #405): one field-less stub record per
/// command, event, aggregate and view on the board, plus one <c>EventModelDefinition</c> that
/// declares the slices through the JasperFx.Events fluent API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why C# and not YAML.</b> jasperfx#955 settled that YAML is not an authoring syntax: the only
/// YAML Bobcat reads is the Event Modeling platform's own, on import. So an import no longer
/// produces a file to hand-edit — it produces the code the design starts from, which specs can be
/// written against immediately and which a Wolverine <c>scaffold</c> command can later fill in.
/// The imported model is red until the behaviour exists, and that is the point.
/// </para>
/// <para>
/// <b>Field-less stubs are what a board can honestly produce.</b> An emlang export carries no
/// field information at all — the board's props are intentionally omitted — so
/// <c>public record AppointmentConfirmed;</c> is the whole truth about a command or event it names.
/// Inventing an <c>Id</c> would be a guess the importer has no basis for, and one that every
/// consumer would then have to un-guess.
/// </para>
/// <para>
/// <b>Everything goes through <see cref="ISourceWriter"/></b>, the same rule Wolverine's
/// <c>scaffold</c> command follows. That is what jasperfx#956's whitespace fixes are for: no blank
/// line before a closing brace, <c>END</c> only as a whole directive, and backticks for double
/// quotes. Hand-rolled string building is how generated code acquires the stray blank lines nobody
/// can explain.
/// </para>
/// <para>
/// <b>Declared by name, not by type, wherever the board only has a name.</b> Every fluent role
/// method has a string overload, and a string becomes a <c>TypeDescriptor(name, name, "")</c> whose
/// empty assembly makes the merge compare by <c>Name</c> — which is exactly how a declared slice
/// folds into the derived one once the real type exists. The generated definition therefore uses
/// <c>typeof</c>-free string overloads for the roles and <c>&lt;T&gt;</c> only where this writer
/// emitted the stub itself and so knows the type will compile.
/// </para>
/// </remarks>
public static class CSharpModelWriter
{
    /// <summary>What <see cref="Write"/> produced: the stub file and the definition file.</summary>
    /// <param name="Stubs">
    /// The stub records, as one file. One file rather than one per type because a board names
    /// dozens and a stub is a single line — a directory of 40 one-line files is harder to review
    /// than one list, and this is a file whose whole purpose is to be reviewed and then edited
    /// apart as the real types grow.
    /// </param>
    /// <param name="Definition">The <c>EventModelDefinition</c> subclass.</param>
    /// <param name="StubCount">How many stub records were written.</param>
    public sealed record Output(string Stubs, string Definition, int StubCount);

    /// <summary>
    /// Render <paramref name="model"/> as C#.
    /// </summary>
    /// <param name="model">The imported model, from <c>EmlangImport.ToCurated</c>.</param>
    /// <param name="namespaceName">
    /// Namespace for both files. Defaults to the model's own <c>Namespace</c>, then to the model
    /// name — never to the global namespace, because a stub record in the global namespace is a
    /// name collision waiting for the second import.
    /// </param>
    public static Output Write(CuratedModelFile model, string? namespaceName = null)
    {
        var ns = namespaceName
                 ?? (string.IsNullOrWhiteSpace(model.Namespace) ? null : model.Namespace)
                 ?? Identifiers.Sanitize(model.Model);

        var stubs = StubNames(model);

        return new Output(writeStubs(ns, model, stubs), writeDefinition(ns, model, stubs), stubs.Count);
    }

    /// <summary>
    /// Every type name the board named, in the order a reader would expect: aggregates, then
    /// commands, then events, then views.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Four kinds, because those are the four the issue names</b> — command, event, aggregate,
    /// view. A handler is deliberately NOT stubbed: a handler is behaviour, and an empty one is the
    /// thing the <c>scaffold</c> command exists to write properly. Nor are external systems, which
    /// are not types at all.
    /// </para>
    /// <para>
    /// <b>Consumed events are included, and that matters.</b> A View slice's <c>consumedEvents</c>
    /// routinely names events emitted by a slice in another chapter, so leaving them out produced a
    /// definition referring to names with no stub — which compiles, because the roles are declared
    /// by string, and then quietly loses the type when someone switches to the generic overload.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> StubNames(CuratedModelFile model)
    {
        var names = new List<string>();

        void take(IEnumerable<string?> candidates)
        {
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;

                var name = Identifiers.Sanitize(candidate!);
                if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal)) names.Add(name);
            }
        }

        take(model.Slices.SelectMany(s => s.Aggregates));
        take(model.Slices.Select(s => s.Command));
        take(model.Slices.SelectMany(s => s.Events));
        take(model.Slices.SelectMany(s => s.ConsumedEvents));
        take(model.Slices.SelectMany(s => s.Messages));
        take(model.Slices.SelectMany(s => s.ReadModels));
        take(model.Slices.SelectMany(s => s.ReadsFrom));

        return names;
    }

    private static string writeStubs(string ns, CuratedModelFile model, IReadOnlyList<string> stubs)
    {
        using var writer = new SourceWriter();

        writer.WriteLine("// Imported from an eventmodelers.ai board by `bobcat import-event-model`.");
        writer.WriteLine("//");
        writer.WriteLine("// These are field-less on purpose: a board carries no field information, so a name is");
        writer.WriteLine("// the whole truth it can tell. Add the fields as the behaviour takes shape — nothing");
        writer.WriteLine("// regenerates this file, so your edits are safe.");
        writer.BlankLine();
        writer.WriteLine($"namespace {ns};");
        writer.BlankLine();

        if (stubs.Count == 0)
        {
            writer.WriteLine("// The board named no commands, events, aggregates or views.");
            return writer.Code();
        }

        foreach (var name in stubs)
        {
            foreach (var line in describe(model, name)) writer.WriteLine($"/// {line}");
            writer.WriteLine($"public record {name};");
            writer.BlankLine();
        }

        return writer.Code();
    }

    /// <summary>
    /// A stub's doc comment: what the board said this type is, which is the one thing the name
    /// alone does not carry.
    /// </summary>
    private static IEnumerable<string> describe(CuratedModelFile model, string name)
    {
        var roles = new List<string>();

        void note(string role, Func<CuratedSlice, IEnumerable<string>> pick)
        {
            var slices = model.Slices
                .Where(s => pick(s).Any(x => Identifiers.Sanitize(x ?? "") == name))
                .Select(s => s.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (slices.Count > 0) roles.Add($"{role} {string.Join(", ", slices)}");
        }

        note("the aggregate behind", s => s.Aggregates);
        note("the command of", s => s.Command is null ? [] : [s.Command]);
        note("emitted by", s => s.Events);
        note("consumed by", s => s.ConsumedEvents);
        note("published by", s => s.Messages);
        note("the read model of", s => s.ReadModels);
        note("read by", s => s.ReadsFrom);

        if (roles.Count == 0) yield break;

        yield return "<summary>";
        foreach (var role in roles) yield return role + ".";
        yield return "</summary>";
    }

    /// <summary>
    /// The <c>EventModelDefinition</c> subclass's name.
    /// </summary>
    /// <remarks>
    /// <b>It must not equal the namespace</b>, which is the default it would otherwise take: the
    /// model name supplies both, so <c>namespace K9Crush { class K9Crush }</c> came out of the
    /// first cut. That compiles, and then every reference to a sibling stub inside it resolves
    /// against the CLASS before the namespace — so <c>K9Crush.SwipeOnDog</c> stops meaning what it
    /// says and the error names a type nobody wrote. An <c>EventModel</c> suffix, only where the
    /// collision is real, keeps the ordinary case reading as the model's own name.
    /// </remarks>
    public static string DefinitionClassName(string modelName, string namespaceName)
    {
        var name = Identifiers.Sanitize(modelName);
        if (name.Length == 0) return "ImportedEventModel";

        var last = namespaceName.Split('.').LastOrDefault() ?? "";

        return string.Equals(name, last, StringComparison.Ordinal) ? name + "EventModel" : name;
    }

    private static string writeDefinition(string ns, CuratedModelFile model, IReadOnlyList<string> stubs)
    {
        var className = DefinitionClassName(model.Model, ns);

        var stubbed = stubs.ToHashSet(StringComparer.Ordinal);

        using var writer = new SourceWriter();

        writer.WriteLine("// Imported from an eventmodelers.ai board by `bobcat import-event-model`.");
        writer.WriteLine("//");
        writer.WriteLine("// This is ordinary C# and nothing regenerates it. The segmentation an import performs is a");
        writer.WriteLine("// set of reported guesses, so a wrong guess is a one-line edit here rather than a re-import.");
        writer.BlankLine();
        writer.WriteLine("using JasperFx.Events.EventModeling;");
        writer.BlankLine();
        writer.WriteLine($"namespace {ns};");
        writer.BlankLine();

        writer.Write($"BLOCK:public class {className} : EventModelDefinition");

        writer.Write($"public override string Name => `{escape(model.Model)}`;");
        writer.BlankLine();

        writer.Write("BLOCK:public override void Configure(EventModelBuilder model)");

        var aggregates = model.Slices
            .SelectMany(s => s.Aggregates)
            .Select(x => Identifiers.Sanitize(x ?? ""))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (aggregates.Count > 0)
        {
            foreach (var aggregate in aggregates) writer.Write(declare("model.Aggregate", aggregate, stubbed));
            writer.BlankLine();
        }

        foreach (var slice in model.Slices)
        {
            writeSlice(writer, slice, stubbed);
            writer.BlankLine();
        }

        writer.FinishBlock();
        writer.FinishBlock();

        return writer.Code();
    }

    private static void writeSlice(ISourceWriter writer, CuratedSlice slice, HashSet<string> stubbed)
    {
        var name = Identifiers.Sanitize(slice.Name);
        if (name.Length == 0) name = "Unnamed";

        if (slice.Notes is { Length: > 0 } notes)
        {
            foreach (var line in notes.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                writer.WriteLine($"// {line.TrimEnd()}");
        }

        // The pattern verb opens the slice, so the descriptor's Pattern is a fact of the call
        // rather than a separate statement that could disagree with it. A slice whose pattern the
        // board did not say falls back to Slice(name) — Pattern stays null, and a null pattern
        // leaves the canvas uncoloured where a wrong one would miscolour it.
        //
        // A GENERIC verb takes its slice name from the type, so it is only usable when that name
        // is the one the board gave the slice. There is no call that renames a slice afterwards,
        // and the name IS the merge key — so getting this wrong would silently split one slice
        // into two rather than failing. When they differ, the slice opens by name and states its
        // pattern and its role separately, which says exactly the same thing.
        var pattern = (slice.Pattern ?? "").Trim().ToLowerInvariant();
        var command = slice.Command is { Length: > 0 } ? Identifiers.Sanitize(slice.Command) : null;
        var view = sanitized(slice.ReadModels).FirstOrDefault();

        string opener;
        string? namedByOpener = null;

        switch (pattern)
        {
            case "command" when command is { Length: > 0 } && command == name && stubbed.Contains(command):
                opener = $"model.Command<{command}>()";
                namedByOpener = command;
                break;

            case "command":
                opener = $"model.Command(`{escape(name)}`)";
                break;

            case "view" when view is { Length: > 0 } && view == name && stubbed.Contains(view):
                opener = $"model.View<{view}>()";
                namedByOpener = view;
                break;

            case "view":
                opener = $"model.View(`{escape(name)}`)";
                break;

            case "automation":
                opener = $"model.Automation(`{escape(name)}`)";
                break;

            case "translation":
                opener = $"model.Translation(`{escape(name)}`)";
                break;

            default:
                opener = $"model.Slice(`{escape(name)}`)";
                break;
        }

        var calls = new List<string>();

        // A Slice(name) opener carries no pattern, so say it outright when the board knew one.
        // Only for the four the enum has words for: anything else the board might say is not a
        // SlicePattern, and forcing it into the nearest member would miscolour the canvas.
        if (opener.StartsWith("model.Slice(", StringComparison.Ordinal)
            && slicePattern(slice.Pattern) is { } declaredPattern)
        {
            calls.Add($".Pattern(SlicePattern.{declaredPattern})");
        }

        if (slice.Domain is { Length: > 0 } domain) calls.Add($".InDomain(`{escape(domain)}`)");
        if (slice.Chapter is { Length: > 0 } chapter) calls.Add($".InChapter(`{escape(chapter)}`)");

        if (slice.Trigger is { } trigger)
        {
            var kind = triggerKind(trigger.Kind);

            if (trigger.Label is { Length: > 0 } label)
            {
                calls.Add(kind is null
                    ? $".TriggeredBy(`{escape(label)}`)"
                    : $".TriggeredBy(`{escape(label)}`, TriggerKind.{kind})");
            }
            else if (kind is not null)
            {
                calls.Add($".TriggeredBy(TriggerKind.{kind})");
            }
        }

        // Stated separately unless the opener already carried it — which is the whole reason
        // namedByOpener exists rather than sniffing the emitted string for a substring.
        if (command is { Length: > 0 } && command != namedByOpener)
        {
            calls.Add(role(".Command", command, stubbed));
        }

        if (slice.Handler is { Length: > 0 } handler)
        {
            // Always by name: a handler is never stubbed (an empty handler is what the scaffold
            // command exists to write), so there is no type to be generic over.
            calls.Add($".HandledBy(`{escape(Identifiers.Sanitize(handler))}`)");
        }

        foreach (var aggregate in sanitized(slice.Aggregates)) calls.Add(role(".Against", aggregate, stubbed));
        foreach (var @event in sanitized(slice.Events)) calls.Add(role(".Emits", @event, stubbed));
        foreach (var message in sanitized(slice.Messages)) calls.Add(role(".Publishes", message, stubbed));
        foreach (var consumed in sanitized(slice.ConsumedEvents)) calls.Add(role(".On", consumed, stubbed));
        foreach (var read in sanitized(slice.ReadsFrom)) calls.Add(role(".Reads", read, stubbed));
        foreach (var projection in sanitized(slice.Projections))
            calls.Add($".Projects(`{escape(projection)}`)");

        foreach (var readModel in sanitized(slice.ReadModels))
        {
            if (readModel == namedByOpener) continue;
            calls.Add(role(".Produces", readModel, stubbed));
        }

        foreach (var external in slice.ExternalSystems)
        {
            if (string.IsNullOrWhiteSpace(external.Name)) continue;

            var direction = (external.Direction ?? "").Trim().Equals("Outbound", StringComparison.OrdinalIgnoreCase)
                ? "Outbound"
                : "Inbound";

            // No endpoint URI: the curated shape carries only a name and a direction, which is
            // all an emlang board names. The fluent overload's third argument is left to whoever
            // edits this file and knows the endpoint.
            calls.Add($".ExternalSystem(`{escape(external.Name)}`, ExternalSystemDirection.{direction})");
        }

        // Spec identities are {Feature}/{Scenario}, and the feature half defaults to the slice
        // name — the same rule the curated shape documents, so a link here is the same string a
        // run's scenario_finished will carry and the two join with no mapping table.
        if (slice.Specifications is { } specifications)
        {
            var feature = specifications.Feature is { Length: > 0 } declared ? declared : slice.Name;

            foreach (var scenario in specifications.Scenarios)
            {
                if (scenario.Name is { Length: > 0 })
                    calls.Add($".LinksToSpecification(`{escape(feature)}/{escape(scenario.Name)}`)");
            }
        }

        foreach (var hotspot in slice.Hotspots)
        {
            if (hotspot is { Length: > 0 }) calls.Add($".Hotspot(`{escape(hotspot)}`)");
        }

        if (calls.Count == 0)
        {
            writer.Write($"{opener};");
            return;
        }

        // One call per line, indented under the opener — the shape the issue's own example uses,
        // and the one a reviewer can read as a list of claims.
        writer.Write(opener);
        writer.IndentionLevel++;
        for (var i = 0; i < calls.Count; i++)
        {
            writer.Write(i == calls.Count - 1 ? calls[i] + ";" : calls[i]);
        }

        writer.IndentionLevel--;
    }

    private static IEnumerable<string> sanitized(IEnumerable<string>? values)
        => (values ?? []).Select(x => Identifiers.Sanitize(x ?? "")).Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// A role call: generic when this writer emitted the stub (so the type is known to compile),
    /// by name otherwise.
    /// </summary>
    /// <remarks>
    /// The two are equivalent to the merge — a string becomes a <c>TypeDescriptor(name, name, "")</c>
    /// whose empty assembly makes comparison fall back to <c>Name</c> (jasperfx#798) — so this is
    /// about the generated file compiling, not about what it means. Reaching for
    /// <c>&lt;Something&gt;</c> that no stub defines would make the import's output not build,
    /// which is the one thing a scaffolded file must never do.
    /// </remarks>
    private static string role(string method, string name, HashSet<string> stubbed)
        => stubbed.Contains(name) ? $"{method}<{name}>()" : $"{method}(`{escape(name)}`)";

    private static string declare(string method, string name, HashSet<string> stubbed)
        => stubbed.Contains(name) ? $"{method}<{name}>()" : $"{method}(`{escape(name)}`)";

    /// <summary>
    /// The board's pattern word as a <c>SlicePattern</c> member, or null for anything the enum has
    /// no word for — in which case the slice carries no pattern at all, because a null pattern
    /// leaves the canvas uncoloured where a wrong one miscolours it.
    /// </summary>
    private static string? slicePattern(string? pattern)
        => (pattern ?? "").Trim().ToLowerInvariant() switch
        {
            "command" => "Command",
            "view" => "View",
            "automation" => "Automation",
            "translation" => "Translation",
            _ => null
        };

    /// <summary>
    /// The board's trigger word as a <c>TriggerKind</c> member, or null when it says nothing this
    /// enum has a word for — in which case the label travels alone rather than being forced into
    /// the nearest member.
    /// </summary>
    private static string? triggerKind(string? kind)
        => (kind ?? "").Trim().ToLowerInvariant() switch
        {
            "http" => "Http",
            "grpc" => "Grpc",
            "messagehandler" or "message" => "MessageHandler",
            "jobscheduler" or "scheduler" or "job" => "JobScheduler",
            "human" => "Human",
            "external" => "External",
            _ => null
        };

    /// <summary>
    /// Escape a string for a C# literal written with backticks. The backtick is
    /// <see cref="ISourceWriter.Write"/>'s stand-in for a double quote, so a literal backtick in
    /// the board's own text would silently become one — hence the swap to a single quote, which is
    /// what a person transcribing a quoted phrase into prose would do anyway.
    /// </summary>
    private static string escape(string value)
        => value.Replace("\\", "\\\\").Replace("`", "'").Replace("\r", "").Replace("\n", " ");

    /// <summary>
    /// Board labels into C# identifiers. Shared with the stub writer so a name can never be
    /// sanitized one way in a record declaration and another way in the call that references it.
    /// </summary>
    public static class Identifiers
    {
        public static string Sanitize(string value)
        {
            var builder = new StringBuilder(value.Length);

            foreach (var character in value)
            {
                if (char.IsLetterOrDigit(character) || character == '_') builder.Append(character);
            }

            var name = builder.ToString();
            if (name.Length == 0) return name;

            // A C# identifier cannot start with a digit, and a board label like "2FA enrolled"
            // would otherwise produce a record declaration that does not parse.
            if (char.IsDigit(name[0])) name = "_" + name;

            return char.IsLower(name[0]) ? char.ToUpperInvariant(name[0]) + name[1..] : name;
        }
    }
}
