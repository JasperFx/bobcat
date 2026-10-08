using System.Text.Json;

namespace Bobcat.EventModel.Emlang;

/// <summary>
/// Reads the eventmodelers.ai platform's own JSON exports into the same <see cref="EmlangBoard"/> the
/// emlang reader produces (bobcat#424), so the import, the stubs and the spec generator work from
/// either source unchanged.
/// </summary>
/// <remarks>
/// <para>Two shapes:</para>
/// <list type="bullet">
/// <item><b>A board backup</b> (<c>boards</c>, <c>nodes</c>, <c>metadata</c>): every element is a
/// node whose content lives in <c>metadata</c>. A <c>CHAPTER</c> node's <c>timelineData</c> lays the
/// chapter out as a grid of ordered columns and typed rows (actor, interaction, swimlane, spec), and
/// a <c>SLICE_BORDER</c> names the column a slice starts at. Columns are read left to right, each
/// top to bottom, so a slice's steps come out in the order the board shows them.</item>
/// <item><b>A <c>config.json</c> / slice export</b> (<c>slices[]</c>): each slice lists its screens,
/// processors, commands, events and read models, and its <c>specifications</c>.</item>
/// </list>
/// <para>
/// Both carry typed fields (<c>UUID</c>, <c>DateTime</c>, …), which become the type sketches a step's
/// props hold, and scenarios whose items carry <c>fields[].example</c> values, which become a test's
/// props. A scenario that expects an error becomes an <c>x:</c> in its <c>then</c>.
/// </para>
/// </remarks>
public static class EventModelersJsonReader
{
    /// <summary>Whether <paramref name="text"/> is JSON at all, before deciding which shape.</summary>
    public static bool LooksLikeJson(string text) => text.TrimStart().StartsWith('{');

    /// <summary>Which of the two shapes <paramref name="json"/> is, or null for neither.</summary>
    public static EventModelFileKind? Sniff(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("nodes", out _) && root.TryGetProperty("metadata", out _))
                return EventModelFileKind.EventModelersBoard;

            if (root.TryGetProperty("slices", out var slices) && slices.ValueKind == JsonValueKind.Array)
                return EventModelFileKind.EventModelersConfig;

            // A single exported slice (slice.json): one slice's elements and specifications
            if (root.TryGetProperty("sliceType", out _)
                || (root.TryGetProperty("title", out _) && (root.TryGetProperty("commands", out _) || root.TryGetProperty("specifications", out _))))
                return EventModelFileKind.EventModelersConfig;

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static EmlangBoard Read(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new EmlangFormatException($"not parseable as JSON: {e.Message}");
        }

        using (document)
        {
            return Sniff(json) switch
            {
                EventModelFileKind.EventModelersBoard => readBoard(document.RootElement),
                EventModelFileKind.EventModelersConfig => readConfig(document.RootElement),
                _ => throw new EmlangFormatException(
                    "not an eventmodelers.ai export: expected a board backup (nodes + metadata) or a config.json (slices[])")
            };
        }
    }

    // ---- config.json ---------------------------------------------------------------------------

    private static EmlangBoard readConfig(JsonElement root)
    {
        var chapters = new List<EmlangChapter>();

        // A slice.json is one slice, with no slices[] around it
        var slices = root.TryGetProperty("slices", out _) ? items(root, "slices") : [root];
        foreach (var slice in slices)
        {
            var name = text(slice, "title");
            var aggregates = items(slice, "aggregates").Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : text(x, "title")).ToList();

            var steps = new List<EmlangStep>();
            foreach (var screen in items(slice, "screens")) steps.Add(step(EmlangElementKind.Screen, "", screen));

            var processor = items(slice, "processors").Select(x => text(x, "title")).FirstOrDefault(x => x.Length > 0);
            foreach (var command in items(slice, "commands"))
            {
                steps.Add(step(EmlangElementKind.Command, "", command, processor) with { Stream = streamOf(command, aggregates) });
            }

            foreach (var @event in items(slice, "events"))
            {
                var stream = streamOf(@event, aggregates);
                steps.Add(step(EmlangElementKind.Event, stream, @event) with { Stream = stream });
            }
            foreach (var view in items(slice, "readmodels")) steps.Add(step(EmlangElementKind.View, "", view));

            var tests = items(slice, "specifications").Select(test).ToList();
            chapters.Add(new EmlangChapter(name, steps, tests));
        }

        return new EmlangBoard(chapters);
    }

    /// <summary>An event's stream: its aggregate, unless that is the platform's "default", then the slice's.</summary>
    private static string streamOf(JsonElement @event, IReadOnlyList<string> sliceAggregates)
    {
        var aggregate = text(@event, "aggregate");
        if (aggregate.Length > 0 && !aggregate.Equals("default", StringComparison.OrdinalIgnoreCase)) return aggregate;
        return sliceAggregates.FirstOrDefault(x => x.Length > 0 && !x.Equals("default", StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    // ---- board backup --------------------------------------------------------------------------

    private static EmlangBoard readBoard(JsonElement root)
    {
        // metadata: { boardId: { nodeId: { meta: {...} } } }, flattened to nodeId -> meta
        var metas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var board in root.GetProperty("metadata").EnumerateObject())
        {
            if (board.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var node in board.Value.EnumerateObject())
            {
                if (node.Value.TryGetProperty("meta", out var meta)) metas[node.Name] = meta;
            }
        }

        var timelines = metas.Values
            .Where(x => text(x, "type") == "CHAPTER" && x.TryGetProperty("timelineData", out _))
            .ToList();

        if (timelines.Count == 0)
        {
            // The platform's older free-form canvas places elements by position, relative to parent
            // nodes, with nothing saying which slice holds them: there is no faithful reading of it
            throw new EmlangFormatException(
                "this board backup has no chapter timeline: it is the older free-form canvas, which places elements by position only. "
                + "Export the board as a config.json from eventmodelers.ai and import that instead.");
        }

        var chapters = new List<EmlangChapter>();
        foreach (var chapter in timelines)
        {
            chapters.AddRange(readChapter(text(chapter, "title"), chapter.GetProperty("timelineData"), metas));
        }

        return new EmlangBoard(chapters);
    }

    private static IEnumerable<EmlangChapter> readChapter(string chapterTitle, JsonElement timeline,
        IReadOnlyDictionary<string, JsonElement> metas)
    {
        var rows = items(timeline, "rows").ToList();
        var rowOrder = rows.Select((x, i) => (Id: text(x, "id"), Index: i)).ToDictionary(x => x.Id, x => x.Index);
        var swimlanes = rows.Where(x => text(x, "type") == "swimlane")
            .ToDictionary(x => text(x, "id"), x => text(x, "label"));

        var columns = items(timeline, "columns").Select(x => text(x, "id")).ToList();
        var cells = items(timeline, "cells")
            .Where(x => text(x, "nodeId").Length > 0)
            .Select(x => (Row: text(x, "rowId"), Col: text(x, "colId"), Node: text(x, "nodeId")))
            .ToList();

        // A slice starts at the column its border names and runs to the next border
        var borders = metas.Values
            .Where(x => text(x, "type") == "SLICE_BORDER")
            .Select(x => (Col: borderColumn(x), Title: text(x, "title")))
            .Where(x => columns.Contains(x.Col))
            .ToDictionary(x => x.Col, x => x.Title);

        // With no borders on the chapter, a column holding a command or a read model starts a slice
        // named after it, and the columns of events that follow stay with it
        string? startsSlice(string column)
        {
            if (borders.Count > 0) return borders.GetValueOrDefault(column);

            return cells.Where(x => x.Col == column)
                .Select(x => metas.GetValueOrDefault(x.Node))
                .Where(x => x.ValueKind == JsonValueKind.Object && text(x, "type") is "COMMAND" or "READMODEL")
                .Select(x => text(x, "title"))
                .FirstOrDefault(x => x.Length > 0);
        }

        var slices = new List<(string Title, List<string> Columns)>();
        foreach (var column in columns)
        {
            var title = startsSlice(column);
            if (title is not null || slices.Count == 0) slices.Add((title ?? chapterTitle, []));
            slices[^1].Columns.Add(column);
        }

        foreach (var (title, sliceColumns) in slices)
        {
            var steps = new List<EmlangStep>();
            var tests = new List<EmlangTest>();
            string? automation = null;

            foreach (var column in sliceColumns)
            {
                foreach (var cell in cells.Where(x => x.Col == column).OrderBy(x => rowOrder.GetValueOrDefault(x.Row)))
                {
                    if (!metas.TryGetValue(cell.Node, out var meta)) continue;

                    switch (text(meta, "type"))
                    {
                        case "SCREEN":
                            steps.Add(step(EmlangElementKind.Screen, "", meta));
                            break;
                        case "AUTOMATION":
                            automation = text(meta, "title");
                            break;
                        case "COMMAND":
                            steps.Add(step(EmlangElementKind.Command, "", meta, automation));
                            automation = null;
                            break;
                        case "EVENT":
                            var lane = swimlanes.GetValueOrDefault(cell.Row) ?? "";
                            steps.Add(step(EmlangElementKind.Event, lane.Equals("Swimlane", StringComparison.OrdinalIgnoreCase) ? "" : lane, meta));
                            break;
                        case "READMODEL":
                            steps.Add(step(EmlangElementKind.View, "", meta));
                            break;
                        case "SCENARIO":
                            if (meta.TryGetProperty("givenWhenThenScenario", out var gwt))
                                tests.AddRange(items(gwt, "scenarios").Select(test));
                            break;
                    }
                }
            }

            if (steps.Count > 0 || tests.Count > 0) yield return new EmlangChapter(title, steps, tests);
        }
    }

    private static string borderColumn(JsonElement meta)
    {
        var col = text(meta, "colId");
        return col.Length > 0 ? col : meta.TryGetProperty("data", out var data) ? text(data, "colId") : "";
    }

    // ---- shared --------------------------------------------------------------------------------

    /// <param name="triggeredBy">The processor that triggers a command, which makes its slice an automation.</param>
    private static EmlangStep step(EmlangElementKind kind, string actor, JsonElement element, string? triggeredBy = null)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in items(element, "fields"))
        {
            var name = text(field, "name");
            if (name.Length > 0) props[name] = sketch(field);
        }

        if (triggeredBy is { Length: > 0 }) props["triggeredBy"] = triggeredBy;

        var identities = items(element, "fields")
            .Where(x => x.TryGetProperty("idAttribute", out var id) && id.ValueKind == JsonValueKind.True)
            .Select(x => text(x, "name"))
            .Where(x => x.Length > 0)
            .ToList();

        return new EmlangStep(kind, actor, text(element, "title"), props)
        {
            Values = props.ToDictionary(x => x.Key, x => (object?)x.Value, StringComparer.Ordinal),
            Stream = kind == EmlangElementKind.Event && actor.Length > 0 ? actor : null,
            Identities = identities
        };
    }

    /// <summary>A declared field's type, in the spellings the stub writer's type table knows.</summary>
    internal static string sketch(JsonElement field)
    {
        var list = text(field, "cardinality").Equals("List", StringComparison.OrdinalIgnoreCase);
        var type = text(field, "type").ToLowerInvariant() switch
        {
            "uuid" => "uuid",
            "int" or "integer" => "int",
            "long" => "long",
            "double" => "double",
            "decimal" => "decimal",
            "boolean" => "boolean",
            "date" => "date",
            "datetime" => "datetime",
            _ => "string"
        };

        return list ? CuratedFieldTypes.StringList : type;
    }

    private static EmlangTest test(JsonElement scenario)
    {
        var then = refs(scenario, "then").ToList();
        if (flag(scenario, "expectError"))
        {
            var reason = text(scenario, "errorDescription");
            then.Add(new EmlangRef(EmlangElementKind.Error, "", reason.Length > 0 ? reason : text(scenario, "title")));
        }

        return new EmlangTest(text(scenario, "title"), refs(scenario, "given").ToList(), refs(scenario, "when").ToList(), then);
    }

    private static IEnumerable<EmlangRef> refs(JsonElement scenario, string section)
    {
        foreach (var item in items(scenario, section))
        {
            var kind = text(item, "type").Replace("SPEC_", "", StringComparison.Ordinal) switch
            {
                "EVENT" => EmlangElementKind.Event,
                "COMMAND" => EmlangElementKind.Command,
                "READMODEL" => EmlangElementKind.View,
                "ERROR" => EmlangElementKind.Error,
                _ => (EmlangElementKind?)null
            };
            if (kind is null) continue;

            // An error item's own title is often a placeholder ("Error-Case"): its description says more
            var label = kind == EmlangElementKind.Error && text(item, "description") is { Length: > 0 } described
                ? described
                : text(item, "title");

            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in items(item, "fields"))
            {
                if (field.TryGetProperty("example", out var example) && example.ValueKind != JsonValueKind.Null)
                {
                    var value = example.ValueKind == JsonValueKind.String ? example.GetString()! : example.GetRawText();
                    if (value.Length > 0) props[text(field, "name")] = value;
                }
            }

            yield return new EmlangRef(kind.Value, "", label)
            {
                Props = props,
                Values = props.ToDictionary(x => x.Key, x => (object?)x.Value, StringComparer.Ordinal)
            };
        }
    }

    /// <summary>A property's items: an array's elements, or a lone object as one item (an export writes both).</summary>
    private static IEnumerable<JsonElement> items(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) yield break;

        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.String) yield return item;
                }
                break;
            case JsonValueKind.Object:
                yield return value;
                break;
        }
    }

    private static string text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool flag(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
