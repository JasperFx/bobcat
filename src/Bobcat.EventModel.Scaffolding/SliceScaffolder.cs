using JasperFx.CodeGeneration;

namespace Bobcat.EventModel.Scaffolding;

/// <summary>
/// The deterministic 80%: a curated slice in, scaffold files out — zero tokens spent on
/// boilerplate; every judgment left as a marked TODO for the AI/human layer.
/// </summary>
public static class SliceScaffolder
{
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
                files[$"{domain}/{slice.Name}.cs"] = withHeader(commandSlice(slice));
                break;

            case "View":
                files[$"{domain}/{slice.Name}.cs"] = withHeader(ScaffoldFrame.Render(new ViewSliceFrame(slice)));
                break;
        }

        return files;
    }

    private static string commandSlice(CuratedSlice slice)
    {
        var frames = new List<ScaffoldFrame>();

        // Event records, fields synthesized from element hints + scenario columns
        foreach (var @event in slice.Events)
        {
            frames.Add(new RecordFrame(@event, fieldsFor(slice, @event),
                slice.Elements.GetValueOrDefault(@event)?.Description));
        }

        // The command record for a Command slice (an Automation is triggered by an event, not a command)
        if (slice.Pattern == "Command" && slice.Command is not null)
        {
            frames.Add(new RecordFrame(slice.Command, fieldsFor(slice, slice.Command)));
        }

        foreach (var aggregate in slice.Aggregates)
        {
            frames.Add(new AggregateFrame(aggregate, slice.Events,
                fieldsFor(slice, aggregate)));
        }

        frames.Add(new WriteModelHandlerFrame(slice,
            maybeNewStream: slice.Pattern == "Command"));

        if (slice.Pattern == "Command" && slice.Trigger?.Kind is "Http" or "Human")
        {
            frames.Add(new EndpointTranslationFrame(slice,
                $"/api/{(slice.Domain ?? "app").ToLowerInvariant()}/{slice.Name.ToLowerInvariant()}"));
        }

        return ScaffoldFrame.Render(frames.ToArray());
    }

    /// <summary>
    /// The identity discipline made mechanical: Feature name and Scenario titles reproduce the
    /// curated identities exactly, and the GWT sub-schema maps 1:1 onto the shipped grammar.
    /// </summary>
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

        var aggregate = slice.Aggregates.FirstOrDefault() ?? "TODO";
        var streamId = "11111111-1111-1111-1111-111111111111";

        foreach (var scenario in specs.Scenarios)
        {
            writer.BlankLine();
            writer.WriteLine($"  @slice:{slice.Name}");
            writer.WriteLine($"  Scenario: {scenario.Name}");
            writer.WriteLine($"    Given no events for {aggregate} \"{streamId}\"");

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
