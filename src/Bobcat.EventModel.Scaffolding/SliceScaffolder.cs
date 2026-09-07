using Bobcat.EventModel.Emlang;
using JasperFx.CodeGeneration;

namespace Bobcat.EventModel.Scaffolding;

/// <summary>
/// The deterministic 80%: a curated slice in, scaffold files out — zero tokens spent on
/// boilerplate; every judgment left as a marked TODO for the AI/human layer.
/// </summary>
public static class SliceScaffolder
{
    /// <summary>
    /// The write model a slice's handler binds to. A slice that declares no <c>aggregates:</c>
    /// still needs a type name, and it must be the same name <see cref="ScaffoldAggregates"/>
    /// emits — a synthesized name nothing declares is a dangling type, and a dangling type fails
    /// the whole project exactly the way a hole in expression position does (issue #226).
    /// </summary>
    public static string AggregateFor(CuratedSlice slice)
        => slice.Aggregates.FirstOrDefault() ?? $"{slice.Name}Model";

    /// <summary>
    /// The event type an Automation slice's handler takes. The board names it as a label
    /// (<c>trigger: { kind: MessageHandler, label: Home check assignment accepted }</c>); with no
    /// label the slice's own first event stands in, and with neither the name is derived from the
    /// slice so two label-less automations in one domain cannot collide on a shared placeholder.
    /// </summary>
    public static string TriggerFor(CuratedSlice slice)
        => slice.Trigger?.Label is { } label
            ? EmlangImport.PascalName(label)
            : slice.Events.FirstOrDefault() ?? $"{slice.Name}Trigger";

    public static IReadOnlyDictionary<string, string> Scaffold(CuratedModelFile model, CuratedSlice slice)
    {
        var files = new Dictionary<string, string>();
        var ns = model.Namespace ?? model.Model;
        var domain = slice.Domain ?? "Shared";

        string withHeader(string body) =>
            $"namespace {ns}.{domain};\n\n{body}";

        switch (slice.Pattern)
        {
            case "Command":
            case "Automation":
                files[$"{domain}/{slice.Name}.cs"] = withHeader(commandSlice(model, slice));
                break;

            case "View":
                files[$"{domain}/{slice.Name}.cs"] = withHeader(ScaffoldFrame.Render(new ViewSliceFrame(slice)));
                break;
        }

        return files;
    }

    /// <summary>
    /// Which shape a Command or Automation slice scaffolds into. One place, because two callers
    /// depend on the answer: the slice's own file, and <see cref="ScaffoldAggregates"/> — which
    /// must know whether a <c>[WriteModel]</c> is going to be bound at all before it emits a type
    /// for one.
    /// </summary>
    private static SliceShape shapeOf(CuratedModelFile model, CuratedSlice slice)
    {
        // The collapsed default (CritterStackSamples#13): an HTTP-triggered command slice IS its
        // endpoint. The two-hop translation + message-handler shape is opt-in for bus-visible
        // commands only — and the model itself opts in (issue #218): a `messages:` entry another
        // slice handles off the bus selects the cascading shape, no flag.
        if (slice.Pattern != "Command" || slice.Trigger?.Kind is not ("Http" or "Human"))
        {
            return SliceShape.WriteModelHandler;
        }

        // The pure translation front (#218): every consequence of this slice is a bus-visible
        // command and it appends nothing itself, so the endpoint is exactly the opt-in two-hop
        // shape — selected by the model rather than by hand.
        return slice.Events.Count == 0 && BusVisibility.Resolve(model, slice).Cascaded.Count == 1
            ? SliceShape.Translation
            : SliceShape.CollapsedEndpoint;
    }

    private static string commandSlice(CuratedModelFile model, CuratedSlice slice)
    {
        var frames = new List<ScaffoldFrame>();
        var command = slice.Command ?? slice.Name;

        var shape = shapeOf(model, slice);
        var collapsed = shape is SliceShape.CollapsedEndpoint or SliceShape.Translation;
        var translation = shape == SliceShape.Translation;

        var visibility = BusVisibility.Resolve(model, slice);

        // Event records, fields synthesized from element hints + scenario columns
        foreach (var @event in slice.Events)
        {
            frames.Add(new RecordFrame(@event, fieldsFor(slice, @event),
                slice.Elements.GetValueOrDefault(@event)?.Description));
        }

        if (collapsed)
        {
            frames.Add(new RecordFrame($"{command}Request", fieldsFor(slice, command)));
            if (!translation) frames.Add(new RecordFrame($"{slice.Name}Response", []));
        }
        else if (slice.Pattern == "Command")
        {
            // The handler's parameter type, whether the model named the command or the slice
            // name stood in for it — either way the record has to exist.
            frames.Add(new RecordFrame(command, fieldsFor(slice, command)));
        }

        // A cascaded message another slice handles is that slice's record; one leaving the
        // system belongs to nobody else, so the publisher's scaffold owns the contract.
        foreach (var message in visibility.Cascaded.Where(x => x.LeavesTheSystem))
        {
            frames.Add(new RecordFrame(message.Name, fieldsFor(slice, message.Name),
                slice.Elements.GetValueOrDefault(message.Name)?.Description));
        }

        var route = $"/api/{(slice.Domain ?? "app").ToLowerInvariant()}/{slice.Name.ToLowerInvariant()}";
        if (translation)
        {
            frames.Add(new EndpointTranslationFrame(slice, route,
                cascadedCommand: visibility.Cascaded[0], warnings: visibility.Warnings));
        }
        else if (collapsed)
        {
            frames.Add(new CollapsedEndpointFrame(slice, route,
                cascaded: visibility.Cascaded, warnings: visibility.Warnings));
        }
        else
        {
            frames.Add(new WriteModelHandlerFrame(slice,
                maybeNewStream: slice.Pattern == "Command",
                cascaded: visibility.Cascaded, warnings: visibility.Warnings,
                publishedBy: BusVisibility.PublishedBy(model, slice)));
        }

        return ScaffoldFrame.Render(frames.ToArray());
    }

    /// <summary>
    /// The identity discipline made mechanical: Feature name and Scenario titles reproduce the
    /// curated identities exactly, and the GWT sub-schema maps 1:1 onto the shipped grammar.
    /// </summary>
    /// <summary>
    /// One file per aggregate, folding EVERY event of EVERY slice that declares it. An aggregate
    /// is a model-level concern, not a slice-level one: nine slices declaring
    /// <c>aggregates: [Appointment]</c> describe ONE type with nine events, and emitting it per
    /// slice produces nine partial duplicates that cannot compile. Same lesson as the feature
    /// files — identity is the key, and a name shared across slices means one artifact.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ScaffoldAggregates(CuratedModelFile model)
    {
        var files = new Dictionary<string, string>();
        var ns = model.Namespace ?? model.Model;

        var declared = new List<(string Name, CuratedSlice Slice)>();
        foreach (var slice in model.Slices)
        {
            if (slice.Aggregates.Count > 0)
            {
                declared.AddRange(slice.Aggregates.Select(name => (name, slice)));
            }
            else if (slice.Pattern is "Command" or "Automation"
                     && shapeOf(model, slice) != SliceShape.Translation)
            {
                // No aggregate declared, but the handler still binds a [WriteModel] of the
                // synthesized name — so that name needs a type as much as a declared one does.
                // The translation shape is the exception: it binds no write model, so emitting
                // one would be a file nothing references.
                declared.Add((AggregateFor(slice), slice));
            }
        }

        var byName = declared.GroupBy(x => x.Name, StringComparer.Ordinal);

        foreach (var group in byName)
        {
            var slices = group.Select(x => x.Slice).ToList();
            var domain = slices.Select(x => x.Domain).FirstOrDefault(x => x is not null) ?? "Shared";

            // Every event any declaring slice emits, in model order, once.
            var events = slices.SelectMany(x => x.Events).Distinct(StringComparer.Ordinal).ToList();
            var fields = slices
                .SelectMany(x => fieldsFor(x, group.Key))
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();

            files[$"{domain}/{group.Key}.cs"] =
                $"namespace {ns}.{domain};\n\n" + ScaffoldFrame.Render(new AggregateFrame(group.Key, events, fields));
        }

        return files;
    }

    /// <summary>
    /// One file per trigger event that arrives from outside this model (issue #223): an
    /// automation whose trigger no slice here emits has nobody to declare its record, and the
    /// generated handler does not compile until somebody does.
    /// </summary>
    /// <remarks>
    /// Model-level rather than per-slice for the same reason an aggregate is (issue #222): a type
    /// name is one artifact. Three automations in a chapter can legally share one inbound
    /// contract, and emitting it into each of their files would be three declarations of one
    /// record — which does not compile either.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ScaffoldTriggerContracts(CuratedModelFile model)
    {
        var files = new Dictionary<string, string>();
        var ns = model.Namespace ?? model.Model;

        var owned = model.Slices
            .Select(slice => (Slice: slice, Origin: TriggerOrigins.Resolve(model, slice)))
            .Where(x => x.Origin is { OwnsTheContract: true })
            .GroupBy(x => x.Origin!.Event, StringComparer.Ordinal);

        foreach (var group in owned)
        {
            var declaring = group.ToList();
            var domain = declaring[0].Slice.Domain ?? "Shared";

            // An inbound edge on ANY declaring slice accounts for the contract; the warning below
            // still names every slice that left it unaccounted for.
            var origin = declaring.Select(x => x.Origin!).FirstOrDefault(x => x.Source == TriggerSource.Inbound)
                         ?? declaring[0].Origin!;

            var fields = declaring
                .SelectMany(x => fieldsFor(x.Slice, group.Key))
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();

            var warnings = declaring.Select(x => TriggerOrigins.Warning(x.Origin!, x.Slice)).OfType<string>().ToList();

            files[$"{domain}/{group.Key}.cs"] = $"namespace {ns}.{domain};\n\n"
                + ScaffoldFrame.Render(new RecordFrame(group.Key, fields, TriggerOrigins.ContractComment(origin), warnings));
        }

        return files;
    }

    /// <summary>
    /// Every file a model scaffolds into. The single door, because the pieces are not independent:
    /// a slice's handler binds a write model <see cref="ScaffoldAggregates"/> emits, and an
    /// automation's trigger record comes from <see cref="ScaffoldTriggerContracts"/>. A caller that
    /// skips one produces a dangling type, and a dangling type fails the whole project (issue #226).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ScaffoldAll(CuratedModelFile model)
    {
        var files = new Dictionary<string, string>();

        foreach (var pair in model.Slices.SelectMany(slice => Scaffold(model, slice))
                     .Concat(ScaffoldAggregates(model))
                     .Concat(ScaffoldTriggerContracts(model))
                     .Concat(ScaffoldFeatures(model)))
        {
            files[pair.Key] = pair.Value;
        }

        return files;
    }

    public static IReadOnlyDictionary<string, string> ScaffoldFeatures(CuratedModelFile model)
    {
        // A feature legally spans slices (and a slice can span features) — group scenarios by
        // the feature half of their identity, or the last slice to write wins and scenarios
        // silently vanish. Found the hard way on the CritterCrush corpus.
        return model.Slices
            .Where(x => x.Specifications is { Scenarios.Count: > 0 })
            .GroupBy(x => x.Specifications!.Feature ?? x.Name)
            .ToDictionary(
                group => $"Features/{group.Key}.feature",
                group => feature(group.Key, group.ToList()));
    }

    private static string feature(string featureName, IReadOnlyList<CuratedSlice> slices)
    {
        var writer = new SourceWriter();
        var first = slices[0];

        if (first.Domain is not null) writer.WriteLine($"@domain:{first.Domain}");
        writer.WriteLine($"Feature: {featureName}");
        if (first.Trigger?.Label is { } featureLabel) writer.WriteLine($"  Triggered by {featureLabel}");

        foreach (var slice in slices)
        {
            writeScenarios(writer, slice);
        }

        return writer.Code();
    }

    private static void writeScenarios(ISourceWriter writer, CuratedSlice slice)
    {
        var specs = slice.Specifications!;

        // A literal "TODO" here names a type the generator cannot resolve (BOBCAT011), which
        // fails the spec project — the .feature half of the same defect as issue #226.
        var aggregate = AggregateFor(slice);

        foreach (var scenario in specs.Scenarios)
        {
            writer.BlankLine();
            writer.WriteLine($"  @slice:{slice.Name}");
            writer.WriteLine($"  Scenario: {scenario.Name}");
            writer.WriteLine($"    Given no events for {aggregate} \"{streamIdFor(scenario.Name)}\"");

            foreach (var given in scenario.Given)
            {
                writer.WriteLine($"    And events for {aggregate}");
                table(writer, "      ", new[] { "Event" }.Concat(given.With.Keys),
                    new[] { given.Event }.Concat(given.With.Values));
            }

            if (scenario.When is { } when)
            {
                writer.WriteLine($"    When {when.Command} is received");
                if (when.With.Count > 0) table(writer, "      ", when.With.Keys, when.With.Values);
            }

            foreach (var then in scenario.Then)
            {
                if (then.Event is not null)
                {
                    writer.WriteLine($"    Then {then.Event} is emitted");
                    if (then.With.Count > 0) table(writer, "      ", then.With.Keys, then.With.Values);
                }
                else if (then.ReadModel is not null)
                {
                    writer.WriteLine($"    Then the {then.ReadModel} read model contains");
                    if (then.Contains.Count > 0) table(writer, "      ", then.Contains.Keys, then.Contains.Values);
                }
                else if (then.ValidationFails is not null)
                {
                    writer.WriteLine($"    Then validation fails with \"{then.ValidationFails}\"");
                    writer.WriteLine("    And no events are emitted");
                }
            }
        }
    }

    /// <summary>
    /// A legible, DISTINCT id per scenario. Scenarios share a store, so reusing one id across
    /// them couples scenarios that should be independent — a stale stream from the last one is
    /// indistinguishable from a bug in this one. Derived from the scenario name so it is stable
    /// across regenerations and readable in a failure message.
    /// </summary>
    private static string streamIdFor(string scenarioName)
    {
        // A small stable hash — no dependency, and the digits stay readable in a failure message.
        uint hash = 2166136261;
        foreach (var c in scenarioName) hash = (hash ^ c) * 16777619;
        var block = (hash % 8999 + 1000).ToString();
        return $"{block}{block}-{block}-{block}-{block}-{block}{block}{block}";
    }

    private static void table(ISourceWriter writer, string indent, IEnumerable<string> headers, IEnumerable<string> values)
    {
        writer.WriteLine($"{indent}| {string.Join(" | ", headers)} |");
        writer.WriteLine($"{indent}| {string.Join(" | ", values)} |");
    }

    /// <summary>
    /// Field synthesis from the model's hints: element field sketches first, then columns the
    /// scenarios exercise. Values that name a type are taken as one; sample values are inferred.
    /// </summary>
    private static List<(string Type, string Name)> fieldsFor(CuratedSlice slice, string typeName)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (slice.Elements.TryGetValue(typeName, out var element))
        {
            foreach (var (name, sketch) in element.Fields)
            {
                fields[name] = inferType(sketch);
            }
        }

        // Columns the scenarios drive through this type are fields it must have
        foreach (var scenario in slice.Specifications?.Scenarios ?? [])
        {
            foreach (var given in scenario.Given.Where(x => x.Event == typeName))
            foreach (var (name, value) in given.With)
            {
                fields.TryAdd(name, inferType(value));
            }

            if (scenario.When?.Command == typeName)
            {
                foreach (var (name, value) in scenario.When.With) fields.TryAdd(name, inferType(value));
            }

            foreach (var then in scenario.Then.Where(x => x.Event == typeName))
            foreach (var (name, value) in then.With)
            {
                fields.TryAdd(name, inferType(value));
            }
        }

        return fields.Select(x => (x.Value, char.ToUpperInvariant(x.Key[0]) + x.Key[1..])).ToList();
    }

    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Guid", "int", "long", "bool", "string", "decimal", "double", "DateTimeOffset", "DateOnly", "TimeSpan" };

    private static string inferType(string sketch)
    {
        if (KnownTypes.Contains(sketch)) return sketch is "guid" or "Guid" ? "Guid" : sketch;
        if (Guid.TryParse(sketch, out _)) return "Guid";
        if (bool.TryParse(sketch, out _)) return "bool";
        if (int.TryParse(sketch, out _)) return "int";
        if (decimal.TryParse(sketch, out _)) return "decimal";
        if (DateTimeOffset.TryParse(sketch, out _)) return "DateTimeOffset";
        return "string";
    }
}

/// <summary>The three shapes a Command or Automation slice scaffolds into.</summary>
internal enum SliceShape
{
    /// <summary>The two-hop OPT-IN (#218): the endpoint mints identity and cascades, binding no write model.</summary>
    Translation,

    /// <summary>The default for an HTTP- or Human-triggered command slice: the endpoint IS the handler.</summary>
    CollapsedEndpoint,

    /// <summary>An automation, or a command taken off the bus: a message handler over the write model.</summary>
    WriteModelHandler
}
