using System.Text.Json;
using Bobcat.EventModel.Scenarios;

namespace Bobcat.EventModel.Emlang;

/// <summary>
/// Reads the eventmodelers.ai platform's own JSON exports — the EM Spec JSON, whose schema is
/// published at <c>dilgerma/event-modeling-spec</c> — into the same <see cref="EmlangBoard"/> the
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
/// <para>
/// <b>Every specification is also lowered to a <see cref="ScenarioModel"/> (bobcat#846)</b>, which is
/// where the export's richer facts go: a <c>context</c> read model the command decides against, an
/// inline <c>QUERY</c> act, a list read model's <c>examples</c> rows and <c>expectEmptyList</c>, a
/// <c>contextNote</c>, a command's <c>apiEndpoint</c> (an HTTP act), <c>generated</c> fields (left out
/// of the comparison) and <c>tag</c> fields (DCB tags). A step is resolved to its element by
/// <c>linkedId</c>/<c>id</c>, following a linked copy to its origin, before its title is read — the
/// title is the fallback, not the key.
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

    private static readonly string[] ElementLists = ["screens", "processors", "commands", "events", "readmodels"];

    private static EmlangBoard readConfig(JsonElement root)
    {
        // A slice.json is one slice, with no slices[] around it
        var slices = (root.TryGetProperty("slices", out _) ? items(root, "slices") : [root]).ToList();

        // Every element of every slice first, so a scenario step anywhere resolves to its element by id —
        // including a step naming a linked copy in another slice (bobcat#846)
        var index = new ElementIndex();
        foreach (var slice in slices)
        {
            var sliceStream = sliceAggregates(slice).FirstOrDefault();
            foreach (var list in ElementLists)
            {
                foreach (var element in items(slice, list)) index.Add(element, sliceStream);
            }
        }

        var chapters = new List<EmlangChapter>();
        foreach (var slice in slices)
        {
            var name = text(slice, "title");
            var aggregates = sliceAggregates(slice);

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
                steps.Add(step(EmlangElementKind.Event, stream ?? "", @event) with { Stream = stream });
            }
            foreach (var view in items(slice, "readmodels")) steps.Add(step(EmlangElementKind.View, "", view));

            var tests = items(slice, "specifications").Select(x => test(x, index)).ToList();

            // bobcat#845: the slice's chapter and pattern are the board's to say, not the import's to infer;
            // bobcat#846: so are its status, its description and who acts on it
            chapters.Add(new EmlangChapter(name, steps, tests)
            {
                BoardChapter = text(slice, "chapter") is { Length: > 0 } chapter ? chapter : null,
                SliceType = PatternOf(text(slice, "sliceType")),
                Status = text(slice, "status") is { Length: > 0 } status ? status : null,
                Description = text(slice, "description") is { Length: > 0 } description ? description : null,
                Actors = items(slice, "actors")
                    .Select(x => new EmlangActor(text(x, "name"), flag(x, "authRequired"),
                        items(x, "rolesRequired").Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!).ToList()))
                    .Where(x => x.Name.Length > 0)
                    .ToList(),
            });
        }

        return new EmlangBoard(chapters);
    }

    /// <summary>The slice's aggregates, as names or as objects; the platform's "default" is nobody's name.</summary>
    private static List<string> sliceAggregates(JsonElement slice)
        => items(slice, "aggregates")
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : text(x, "title"))
            .Where(isAggregateName)
            .ToList();

    /// <summary>
    /// The pattern an EM Spec <c>sliceType</c> declares, in the curated vocabulary (bobcat#845):
    /// <c>STATE_CHANGE</c> → Command, <c>STATE_VIEW</c> → View, <c>AUTOMATION</c> → Automation,
    /// <c>TRANSLATION</c> → Translation. Null for <c>UNDEFINED</c>, an empty value or anything else,
    /// which leaves the pattern to the import's inference.
    /// </summary>
    public static string? PatternOf(string sliceType) => sliceType.Trim().ToUpperInvariant() switch
    {
        "STATE_CHANGE" => "Command",
        "STATE_VIEW" => "View",
        "AUTOMATION" => "Automation",
        "TRANSLATION" => "Translation",
        _ => null
    };

    /// <summary>An event's stream: its aggregate, unless that is the platform's "default", then the slice's; null for neither.</summary>
    private static string? streamOf(JsonElement @event, IReadOnlyList<string> sliceAggregates)
        => aggregateOf(@event) ?? sliceAggregates.FirstOrDefault(isAggregateName);

    /// <summary>
    /// The aggregate an element names, or null when it names none. The platform writes
    /// <c>"default"</c> wherever nobody filled the field in, so it is a sentinel, not a name, and both
    /// readers treat it as absent (bobcat#845).
    /// </summary>
    private static string? aggregateOf(JsonElement element)
    {
        var aggregate = text(element, "aggregate");
        return isAggregateName(aggregate) ? aggregate : null;
    }

    private static bool isAggregateName(string aggregate)
        => aggregate.Length > 0 && !aggregate.Equals("default", StringComparison.OrdinalIgnoreCase);

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

        // A board's nodes, keyed by node id, so a scenario step resolves to its element (a linked copy to its origin)
        var index = new ElementIndex();
        foreach (var (nodeId, meta) in metas) index.AddNode(nodeId, meta);

        var chapters = new List<EmlangChapter>();
        foreach (var chapter in timelines)
        {
            chapters.AddRange(readChapter(text(chapter, "title"), chapter.GetProperty("timelineData"), metas, index));
        }

        return new EmlangBoard(chapters);
    }

    private static IEnumerable<EmlangChapter> readChapter(string chapterTitle, JsonElement timeline,
        IReadOnlyDictionary<string, JsonElement> metas, ElementIndex index)
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
            .Select(x => (Col: borderColumn(x), Meta: x))
            .Where(x => columns.Contains(x.Col))
            .ToDictionary(x => x.Col, x => x.Meta);

        // With no borders on the chapter, a column holding a command or a read model starts a slice
        // named after it, and the columns of events that follow stay with it
        string? startsSlice(string column)
        {
            if (borders.Count > 0) return borders.TryGetValue(column, out var border) ? text(border, "title") : null;

            return cells.Where(x => x.Col == column)
                .Select(x => metas.GetValueOrDefault(x.Node))
                .Where(x => x.ValueKind == JsonValueKind.Object && text(x, "type") is "COMMAND" or "READMODEL")
                .Select(x => text(x, "title"))
                .FirstOrDefault(x => x.Length > 0);
        }

        var slices = new List<(string Title, JsonElement? Border, List<string> Columns)>();
        foreach (var column in columns)
        {
            var title = startsSlice(column);
            if (title is not null || slices.Count == 0)
            {
                slices.Add((title ?? chapterTitle, borders.TryGetValue(column, out var border) ? border : null, []));
            }

            slices[^1].Columns.Add(column);
        }

        foreach (var (title, border, sliceColumns) in slices)
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
                            steps.Add(step(EmlangElementKind.Screen, "", meta, sourceId: cell.Node));
                            break;
                        case "AUTOMATION":
                            automation = text(meta, "title");
                            break;
                        case "COMMAND":
                            steps.Add(step(EmlangElementKind.Command, "", meta, automation, cell.Node) with { Stream = aggregateOf(meta) });
                            automation = null;
                            break;
                        case "EVENT":
                            // The element's own aggregate first (the same field, and the same "default"
                            // sentinel, as a config.json's: bobcat#845), else the swimlane it sits in
                            var lane = swimlanes.GetValueOrDefault(cell.Row) ?? "";
                            var stream = aggregateOf(meta) ?? (lane.Equals("Swimlane", StringComparison.OrdinalIgnoreCase) ? "" : lane);
                            steps.Add(step(EmlangElementKind.Event, stream, meta, sourceId: cell.Node));
                            break;
                        case "READMODEL":
                            steps.Add(step(EmlangElementKind.View, "", meta, sourceId: cell.Node));
                            break;
                        case "SCENARIO":
                            if (meta.TryGetProperty("givenWhenThenScenario", out var gwt))
                                tests.AddRange(items(gwt, "scenarios").Select(x => test(x, index)));
                            break;
                    }
                }
            }

            // The timeline is the board's chapter; the slice border only names the slice (bobcat#845)
            if (steps.Count > 0 || tests.Count > 0)
            {
                yield return new EmlangChapter(title, steps, tests)
                {
                    BoardChapter = chapterTitle,
                    Status = border is { } b && text(b, "sliceStatus") is { Length: > 0 } status ? status : null,
                    Description = border is { } d && text(d, "description") is { Length: > 0 } description ? description : null,
                };
            }
        }
    }

    private static string borderColumn(JsonElement meta)
    {
        var col = text(meta, "colId");
        return col.Length > 0 ? col : meta.TryGetProperty("data", out var data) ? text(data, "colId") : "";
    }

    // ---- shared --------------------------------------------------------------------------------

    /// <param name="triggeredBy">The processor that triggers a command, which makes its slice an automation.</param>
    /// <param name="sourceId">The board's id for the element, when the shape keeps it apart from the element (a backup's node id).</param>
    private static EmlangStep step(EmlangElementKind kind, string actor, JsonElement element, string? triggeredBy = null, string? sourceId = null)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in items(element, "fields"))
        {
            var name = text(field, "name");
            if (name.Length > 0) props[name] = sketch(field);
        }

        if (triggeredBy is { Length: > 0 }) props["triggeredBy"] = triggeredBy;

        var id = sourceId ?? text(element, "id");
        return new EmlangStep(kind, actor, text(element, "title"), props)
        {
            Values = props.ToDictionary(x => x.Key, x => (object?)x.Value, StringComparer.Ordinal),
            Stream = kind == EmlangElementKind.Event && actor.Length > 0 ? actor : null,
            Identities = flagged(element, "idAttribute"),
            Generated = flagged(element, "generated"),
            TagFields = flagged(element, "tag"),
            ApiEndpoint = text(element, "apiEndpoint") is { Length: > 0 } endpoint ? endpoint : null,
            Description = text(element, "description") is { Length: > 0 } description ? description : null,
            SourceId = id.Length > 0 ? id : null,
        };
    }

    /// <summary>The names of the element's fields that carry <paramref name="flagName"/> as true.</summary>
    private static List<string> flagged(JsonElement element, string flagName)
        => items(element, "fields")
            .Where(x => flag(x, flagName))
            .Select(x => text(x, "name"))
            .Where(x => x.Length > 0)
            .ToList();

    /// <summary>
    /// A declared field's type, in the spellings the stub writer's type table knows. The EM Spec enum is
    /// String, Boolean, Int, Long, Double, Decimal, Number, Date, DateTime, UUID and Custom; a
    /// <c>Custom</c> field (nested <c>subfields</c>) sketches as text until bobcat#851 writes it as a type.
    /// </summary>
    internal static string sketch(JsonElement field)
    {
        var list = text(field, "cardinality").Equals("List", StringComparison.OrdinalIgnoreCase);
        var type = text(field, "type").ToLowerInvariant() switch
        {
            "uuid" => "uuid",
            "int" or "integer" => "int",
            "long" => "long",
            "double" => "double",
            "decimal" or "number" => "decimal",
            "boolean" => "boolean",
            "date" => "date",
            "datetime" => "datetime",
            _ => "string"
        };

        return list ? CuratedFieldTypes.StringList : type;
    }

    // ---- scenarios -----------------------------------------------------------------------------

    /// <summary>
    /// One specification as both shapes: the element-naming <see cref="EmlangTest"/> the writer reads
    /// today, and the <see cref="ScenarioModel"/> it carries (bobcat#846).
    /// </summary>
    private static EmlangTest test(JsonElement scenario, ElementIndex index)
    {
        var title = text(scenario, "title");
        var expectError = flag(scenario, "expectError");
        var reason = text(scenario, "errorDescription") is { Length: > 0 } described ? described : title;

        // The element-naming shape, as before
        var then = refs(scenario, "then", index).ToList();
        if (expectError) then.Add(new EmlangRef(EmlangElementKind.Error, "", reason));
        var given = refs(scenario, "given", index).Concat(refs(scenario, "context", index).Where(x => x.Kind == EmlangElementKind.View)).ToList();
        var when = refs(scenario, "when", index).ToList();

        // The typed shape
        var givenEvents = new List<GivenEvent>();
        var context = new List<ContextReadModel>();
        foreach (var item in items(scenario, "given").Concat(items(scenario, "context")))
        {
            var resolved = index.Resolve(item);
            var values = examples(item);
            switch (kindOf(item))
            {
                case EmlangElementKind.Event:
                    givenEvents.Add(new GivenEvent(resolved?.Title ?? text(item, "title"), values)
                    {
                        Stream = resolved?.Stream,
                        Tags = tags(resolved, values),
                    });
                    break;
                case EmlangElementKind.View:
                    var rows = rowsOf(item);
                    context.Add(new ContextReadModel(resolved?.Title ?? text(item, "title"), values) { Rows = rows.Count > 0 ? rows : null });
                    break;
            }
        }

        ScenarioAct? act = null;
        foreach (var item in items(scenario, "when"))
        {
            var resolved = index.Resolve(item);
            var name = resolved?.Title ?? text(item, "title");
            var values = examples(item);
            var kind = text(item, "type").Replace("SPEC_", "", StringComparison.Ordinal);

            if (kind == "QUERY")
            {
                // An inline query of a read model: the outcome depends on how the view is asked
                act ??= new ReadModelQueried(name, values);
            }
            else if (kind == "COMMAND")
            {
                act ??= resolved?.ApiEndpoint is { } endpoint
                    ? httpRequest(endpoint, name, values)
                    : new MessageReceived(name, values);
            }
        }

        var outcomes = new List<ScenarioOutcome>();
        foreach (var item in items(scenario, "then"))
        {
            var resolved = index.Resolve(item);
            var name = resolved?.Title ?? text(item, "title");
            var values = examples(item);
            switch (kindOf(item))
            {
                case EmlangElementKind.Event:
                    outcomes.Add(new EventEmitted(name, values)
                    {
                        Stream = resolved?.Stream,
                        Tags = tags(resolved, values),
                        IgnoredMembers = resolved?.Generated ?? [],
                    });
                    break;

                case EmlangElementKind.View:
                    // bobcat#846, decided 2026-11-27: rows are the exact set in any order, and an empty list is
                    // "no items"; the rows live on the scenario (the platform's GWT shape) or on the step (its
                    // storyline shape), whichever is there
                    var rows = rowsOf(item);
                    if (rows.Count == 0) rows = rowsOf(scenario);
                    var emptyList = flag(scenario, "expectEmptyList") || flag(item, "expectEmptyList");

                    outcomes.Add(emptyList
                        ? new ReadModelOutcome(name, ReadModelShape.None, MatchMode.Exact, values)
                        : rows.Count > 0
                            ? new ReadModelOutcome(name, ReadModelShape.Rows, MatchMode.Exact, values) { Rows = rows }
                            : new ReadModelOutcome(name, values.Count > 0 ? ReadModelShape.Partial : ReadModelShape.Single, MatchMode.Subset, values));
                    break;

                case EmlangElementKind.Error:
                    outcomes.Add(new Refusal(errorLabel(item), RefusalKind.Refused, values.Values.Where(x => x.Length > 0).ToList()));
                    break;
            }
        }

        if (expectError && !outcomes.Any(x => x is Refusal))
        {
            outcomes.Add(new Refusal(reason, RefusalKind.Refused, []));
        }

        var model = new ScenarioModel(title)
        {
            SourceId = text(scenario, "id") is { Length: > 0 } id ? id : null,
            Note = text(scenario, "contextNote") is { Length: > 0 } note ? note : null,
            GivenEvents = givenEvents,
            Context = context,
            Act = act,
            Outcomes = outcomes,
        };

        return new EmlangTest(title, given, when, then) { Model = model };
    }

    /// <summary>
    /// A command's <c>apiEndpoint</c> as an HTTP act: <c>POST /carts/{cartId}/items</c> is the verb and the
    /// route; a bare route is a POST. The route's <c>{holes}</c> take the values the example gives those fields.
    /// </summary>
    internal static HttpRequest httpRequest(string endpoint, string command, IReadOnlyDictionary<string, string> values)
    {
        var trimmed = endpoint.Trim();
        var space = trimmed.IndexOf(' ');
        var verb = space > 0 ? trimmed[..space].ToUpperInvariant() : "";
        var route = space > 0 ? trimmed[(space + 1)..].Trim() : trimmed;
        if (verb is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD"))
        {
            verb = "POST";
            route = trimmed;
        }

        var routeValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (route.Contains("{" + name + "}", StringComparison.OrdinalIgnoreCase)) routeValues[name] = value;
        }

        return new HttpRequest(verb, route, command, values) { RouteValues = routeValues };
    }

    private static IReadOnlyDictionary<string, string> tags(ElementIndex.Entry? element, IReadOnlyDictionary<string, string> values)
    {
        if (element is null || element.Tags.Count == 0) return ScenarioModel.None;

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in element.Tags)
        {
            if (values.TryGetValue(tag, out var value)) tags[tag] = value;
        }

        return tags;
    }

    private static EmlangElementKind? kindOf(JsonElement item) => text(item, "type").Replace("SPEC_", "", StringComparison.Ordinal) switch
    {
        "EVENT" => EmlangElementKind.Event,
        "COMMAND" => EmlangElementKind.Command,
        "READMODEL" => EmlangElementKind.View,
        "ERROR" => EmlangElementKind.Error,
        _ => null
    };

    /// <summary>An error item's own title is often a placeholder ("Error-Case"): its description says more.</summary>
    private static string errorLabel(JsonElement item)
        => text(item, "description") is { Length: > 0 } described ? described : text(item, "title");

    /// <summary>The values an item's fields carry as examples, by field name; a field with no example says nothing.</summary>
    private static Dictionary<string, string> examples(JsonElement item)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in items(item, "fields"))
        {
            if (field.TryGetProperty("example", out var example) && example.ValueKind != JsonValueKind.Null)
            {
                var value = example.ValueKind == JsonValueKind.String ? example.GetString()! : example.GetRawText();
                if (value.Length > 0 && text(field, "name") is { Length: > 0 } name) props[name] = value;
            }
        }

        return props;
    }

    /// <summary>A list read model's expected rows (<c>examples[]</c>, each an object keyed by field name), as text.</summary>
    private static List<IReadOnlyDictionary<string, string>> rowsOf(JsonElement owner)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var row in items(owner, "examples"))
        {
            if (row.ValueKind != JsonValueKind.Object) continue;

            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var cell in row.EnumerateObject())
            {
                cells[cell.Name] = cell.Value.ValueKind switch
                {
                    JsonValueKind.String => cell.Value.GetString() ?? "",
                    JsonValueKind.Null => "",
                    _ => cell.Value.GetRawText(),
                };
            }

            rows.Add(cells);
        }

        return rows;
    }

    private static IEnumerable<EmlangRef> refs(JsonElement scenario, string section, ElementIndex index)
    {
        foreach (var item in items(scenario, section))
        {
            var kind = kindOf(item);
            if (kind is null) continue;

            // Resolved to the element by id where the export names one, so a renamed or linked element
            // still lands on its type (bobcat#846); the step's own title is the fallback
            var label = kind == EmlangElementKind.Error
                ? errorLabel(item)
                : index.Resolve(item)?.Title ?? text(item, "title");

            var props = examples(item);
            yield return new EmlangRef(kind.Value, "", label)
            {
                Props = props,
                Values = props.ToDictionary(x => x.Key, x => (object?)x.Value, StringComparer.Ordinal)
            };
        }
    }

    /// <summary>
    /// The board's elements by id, so a scenario step is resolved to the element it names rather than
    /// matched by title (bobcat#846). A linked copy (<c>elementCopy</c> with a <c>linkedId</c>, or a node's
    /// <c>linkedTo</c>) is followed to its origin, which is where scenarios belong.
    /// </summary>
    internal sealed class ElementIndex
    {
        internal sealed record Entry(string Title, string Type, string? Stream, IReadOnlyList<string> Generated, IReadOnlyList<string> Tags, string? ApiEndpoint, string? Origin);

        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

        /// <summary>A config.json element: keyed by its <c>id</c>, with its <c>linkedId</c> as the origin when it is a copy.</summary>
        public void Add(JsonElement element, string? sliceStream)
        {
            var id = text(element, "id");
            if (id.Length == 0) return;

            var linked = text(element, "linkedId");
            var origin = flag(element, "elementCopy") && linked.Length > 0 && linked != id ? linked : null;
            _entries.TryAdd(id, entry(element, aggregateOf(element) ?? sliceStream, origin));
        }

        /// <summary>A board backup node: keyed by node id, with <c>linkedTo</c> as the origin.</summary>
        public void AddNode(string nodeId, JsonElement meta)
        {
            if (text(meta, "type") is not ("COMMAND" or "EVENT" or "READMODEL" or "SCREEN" or "AUTOMATION")) return;

            var origin = text(meta, "linkedTo");
            _entries.TryAdd(nodeId, entry(meta, aggregateOf(meta), origin.Length > 0 && origin != nodeId ? origin : null));
        }

        private static Entry entry(JsonElement element, string? stream, string? origin)
            => new(text(element, "title"), text(element, "type"), stream, flagged(element, "generated"), flagged(element, "tag"),
                text(element, "apiEndpoint") is { Length: > 0 } endpoint ? endpoint : null, origin);

        /// <summary>The element a scenario step names, by its <c>linkedId</c> or <c>id</c>, followed to its origin; null when the step names none the board has.</summary>
        public Entry? Resolve(JsonElement item)
        {
            var key = text(item, "linkedId") is { Length: > 0 } linked ? linked : text(item, "id");
            if (key.Length == 0 || !_entries.TryGetValue(key, out var found)) return null;

            // A copy's origin may itself be a copy; a loop in the data ends here rather than in a hang
            for (var hops = 0; hops < 8 && found.Origin is { } origin && _entries.TryGetValue(origin, out var next); hops++)
            {
                found = next;
            }

            return found;
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
