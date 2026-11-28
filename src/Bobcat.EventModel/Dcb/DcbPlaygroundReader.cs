using System.Globalization;
using System.Text.Json;
using Bobcat.EventModel.Emlang;
using Bobcat.EventModel.Scenarios;
using JasperFx.Events.EventModeling;

namespace Bobcat.EventModel.Dcb;

/// <summary>
/// Reads a dcb.events Playground model (<c>https://dcb.events/schemas/model/v10.json</c>, bobcat#849) into the
/// imported model: the one published notation that carries the Dynamic Consistency Boundary primitives
/// (jasperfx#1297) — tag types, tagged events, projections read by tag, commands that <c>require</c> a read
/// <c>else reject</c>, untagged projections, and scenarios whose <c>then</c> is an event, nothing, or a rejection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads declared, boundary derived.</b> A command never states its consistency boundary; what it reads does.
/// Every projection read in the command's <c>boundary</c> or its <c>conditions</c> (and anywhere else an operand
/// reads one) becomes a <see cref="CuratedRead"/> by the tags it is given, an entity binding a decider-model read
/// by the entity's identity tag, and JasperFx computes the query from those. A projection with no tags that a
/// command reads is an untagged projection: outside any boundary, informational.
/// </para>
/// <para>
/// <b>Lowered straight to the imported model</b>, not through the emlang board: a Playground model has no
/// screens, chapters or step order to segment, and the aggregate inference has nothing to infer where a decision
/// is made by tag (<see cref="CuratedSlice.Dcb"/>). A command's <c>feature</c> is its chapter, so a model that
/// groups its commands gets a definition per feature.
/// </para>
/// <para>
/// <b>Tag names follow JasperFx</b>: the tag type <c>CourseId</c> is the tag <c>courseId</c>
/// (<see cref="EventTagDescriptor.NameFor(string)"/>), whatever a property or a projection tag is called, so the
/// declared catalogue merges with what Wolverine derives from the registry.
/// </para>
/// </remarks>
public static class DcbPlaygroundReader
{
    /// <summary>Whether <paramref name="root"/> is a Playground model envelope: it says <c>dcbModelVersion</c>, or its <c>$schema</c> is dcb.events' model schema.</summary>
    public static bool Sniff(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("dcbModelVersion", out _)) return true;

        return root.TryGetProperty("$schema", out var schema) && schema.ValueKind == JsonValueKind.String
               && (schema.GetString() ?? "").Contains("dcb.events/schemas/model", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Lower the envelope to the imported model. <paramref name="modelName"/> null takes the envelope's own
    /// <c>name</c>, as an identifier.
    /// </summary>
    /// <exception cref="EmlangFormatException">Not JSON, or not a Playground model.</exception>
    public static EmlangImportResult ToCurated(string json, string? modelName = null, string? @namespace = null)
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
            var root = document.RootElement;
            if (!Sniff(root))
            {
                throw new EmlangFormatException("not a dcb.events Playground model: expected dcbModelVersion and the model's definitions");
            }

            return new Lowering(root, modelName, @namespace).Run();
        }
    }

    /// <summary>One lowering, holding the envelope's definitions by name while the slices are built.</summary>
    private sealed class Lowering
    {
        private readonly JsonElement _root;
        private readonly ImportedEventModel _model;
        private readonly List<string> _report = [];
        private readonly List<ImportCallout> _callouts = [];

        private readonly Dictionary<string, CustomType> _customTypes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonElement> _events = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonElement> _projections = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonElement> _entities = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CuratedSlice> _slices = new(StringComparer.Ordinal);

        /// <summary>A value type the model declares: a tag (scalar, <c>isTag</c>), another scalar, a composite record, or an enum.</summary>
        private sealed record CustomType(string Name, bool IsTag, string Primitive, IReadOnlyList<(string Name, string Type)> Fields, bool IsEnum);

        public Lowering(JsonElement root, string? modelName, string? @namespace)
        {
            _root = root;
            var name = modelName ?? EmlangImport.PascalName(text(root, "name"));
            if (name.Length == 0) name = "DcbModel";
            _model = new ImportedEventModel { Schema = 1, Model = name, Namespace = @namespace };
        }

        public EmlangImportResult Run()
        {
            foreach (var type in items(_root, "customTypeDefinitions")) indexCustomType(type);
            foreach (var @event in items(_root, "eventDefinitions")) _events[text(@event, "name")] = @event;
            foreach (var projection in items(_root, "projectionDefinitions")) _projections[text(projection, "name")] = projection;
            foreach (var entity in items(_root, "entityDefinitions")) _entities[text(entity, "name")] = entity;

            foreach (var command in items(_root, "commandDefinitions")) commandSlice(command);
            foreach (var projection in _projections.Values) projectionSlice(projection);

            // An event no command emits still needs a stub: the first projection folding it carries its fields
            foreach (var (name, @event) in _events)
            {
                if (_model.Slices.Any(s => s.Events.Contains(name) || s.Elements.ContainsKey(name))) continue;

                var consumer = _model.Slices.FirstOrDefault(s => s.ConsumedEvents.Contains(name));
                if (consumer is not null) describeEvent(consumer, @event);
            }

            foreach (var scenario in items(_root, "scenarioDefinitions")) commandScenario(scenario);
            foreach (var scenario in items(_root, "projectionScenarioDefinitions")) projectionScenario(scenario);

            _report.Add($"dcb.events model '{text(_root, "name")}': {_slices.Values.Count(x => x.Pattern == "Command")} command slice(s), "
                        + $"{_projections.Count} projection(s), {_model.Tags.Count} tag(s), "
                        + $"{_model.Slices.Sum(x => x.Specifications?.Scenarios.Count ?? 0)} scenario(s).");

            return new EmlangImportResult(_model, _report) { Callouts = _callouts };
        }

        // ---- types -------------------------------------------------------------------------------

        private void indexCustomType(JsonElement type)
        {
            var name = text(type, "name");
            if (name.Length == 0) return;

            var fields = items(type, "properties")
                .Select(x => (Name: text(x, "name"), Type: text(x, "propertyType")))
                .Where(x => x.Name.Length > 0)
                .ToList();
            var schema = type.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
            var isEnum = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("enum", out _);
            var primitive = schema.ValueKind == JsonValueKind.Object ? primitiveOf(text(schema, "type")) : "string";
            var isTag = flag(type, "isTag");

            _customTypes[name] = new CustomType(name, isTag, primitive, fields, isEnum);
            if (isTag) _model.Tags[EventTagDescriptor.NameFor(name)] = primitive;
        }

        /// <summary>A Playground type name as a C# type: the three primitives, a tag or scalar's primitive, a composite's record name.</summary>
        private string typeOf(string typeName)
        {
            if (_customTypes.TryGetValue(typeName, out var custom))
            {
                return custom.Fields.Count > 0 ? CSharpModelWriter.Identifiers.Sanitize(custom.Name) : custom.Primitive;
            }

            return primitiveOf(typeName);
        }

        private static string primitiveOf(string typeName) => typeName.Trim().ToLowerInvariant() switch
        {
            "boolean" => "bool",
            "integer" => "int",
            "number" => "decimal",
            _ => "string",
        };

        /// <summary>The tag a property of a tag type is, by JasperFx's name for the type; null for a property of any other type.</summary>
        private string? tagOf(string typeName)
            => _customTypes.TryGetValue(typeName, out var custom) && custom.IsTag ? EventTagDescriptor.NameFor(custom.Name) : null;

        /// <summary>A declared field from a <c>PropertyDefinition</c> or a composite's field.</summary>
        private CuratedField field(string name, string typeName, bool optional, bool list)
        {
            var custom = _customTypes.GetValueOrDefault(typeName);
            return new CuratedField
            {
                Name = name,
                Type = typeOf(typeName),
                Many = list,
                Optional = optional,
                Tag = tagOf(typeName),
                Subfields = custom is { Fields.Count: > 0 }
                    ? custom.Fields.Select(x => field(x.Name, x.Type, optional: false, list: false)).ToList()
                    : [],
            };
        }

        private CuratedElement elementOf(CuratedSlice slice, string typeName)
        {
            if (!slice.Elements.TryGetValue(typeName, out var element))
            {
                element = new CuratedElement();
                slice.Elements[typeName] = element;
            }

            return element;
        }

        private void declare(CuratedElement element, CuratedField declared)
        {
            element.Fields.TryAdd(declared.Name, declared.Many ? CuratedFieldTypes.StringList : declared.Type);
            element.DeclaredFields.TryAdd(declared.Name, declared);
        }

        private void describeProperties(CuratedSlice slice, string typeName, JsonElement owner, string? description = null)
        {
            var element = elementOf(slice, typeName);
            element.Description ??= description;
            foreach (var property in items(owner, "properties"))
            {
                var name = text(property, "name");
                if (name.Length == 0) continue;
                declare(element, field(name, text(property, "propertyType"), flag(property, "isOptional"), flag(property, "isList")));
            }
        }

        private void describeEvent(CuratedSlice slice, JsonElement @event)
        {
            var name = text(@event, "name");
            describeProperties(slice, name, @event);
            var tags = eventTags(@event);
            if (tags.Count > 0) slice.EventTags[name] = tags;
        }

        /// <summary>The tags an event carries: its <c>tags</c> property paths, each read as the tag of the property's type.</summary>
        private List<string> eventTags(JsonElement @event)
        {
            var properties = items(@event, "properties").ToDictionary(x => text(x, "name"), x => text(x, "propertyType"), StringComparer.Ordinal);
            var tags = new List<string>();
            foreach (var path in items(@event, "tags").Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!))
            {
                var tag = properties.TryGetValue(path, out var type) ? tagOf(type) ?? path : path;
                if (tag.Length > 0 && !tags.Contains(tag)) tags.Add(tag);
            }

            return tags;
        }

        // ---- commands ----------------------------------------------------------------------------

        private void commandSlice(JsonElement command)
        {
            var name = text(command, "name");
            if (name.Length == 0) return;

            var feature = text(command, "feature");
            var slice = new CuratedSlice
            {
                Name = name,
                Command = name,
                Pattern = "Command",
                Dcb = true,
                Chapter = feature.Length > 0 ? feature : null,
                Notes = $"From the dcb.events model '{text(_root, "name")}'" + (feature.Length > 0 ? $", feature '{feature}'." : "."),
            };

            describeProperties(slice, name, command, text(command, "description") is { Length: > 0 } d ? d : null);

            foreach (var emission in items(command, "publishes"))
            {
                var eventName = text(emission, "name");
                if (eventName.Length == 0) continue;
                if (!slice.Events.Contains(eventName)) slice.Events.Add(eventName);
                if (_events.TryGetValue(eventName, out var @event)) describeEvent(slice, @event);
            }

            foreach (var binding in items(command, "boundary"))
            {
                if (text(binding, "entity") is { Length: > 0 } entity) entityRead(slice, entity, binding);
                else if (text(binding, "projection") is { Length: > 0 } projection) projectionRead(slice, projection, binding);
            }

            // Every projection read in place — a condition's operand, an emission's value, a guard — is a read too
            foreach (var read in inlineReads(command))
            {
                projectionRead(slice, text(read, "projection"), read);
            }

            foreach (var rule in items(command, "conditions"))
            {
                var rejection = text(rule, "rejection");
                if (rejection.Length > 0 && !slice.RejectionReasons.Contains(rejection)) slice.RejectionReasons.Add(rejection);
            }

            _model.Slices.Add(slice);
            _slices[name] = slice;
            _report.Add($"command '{name}': Command slice, {slice.Reads.Count} read(s) in its boundary, "
                        + $"{slice.Events.Count} event(s), {slice.RejectionReasons.Count} rejection(s)"
                        + (feature.Length > 0 ? $", feature '{feature}'." : "."));
        }

        /// <summary>An entity binding is a decider-model read by the entity's identity tag, whose value the bound <c>id</c> supplies.</summary>
        private void entityRead(CuratedSlice slice, string entityName, JsonElement binding)
        {
            var entity = _entities.GetValueOrDefault(entityName);
            var identifierType = text(entity, "identifierType") is { Length: > 0 } declared ? declared : entityName + "Id";
            var tag = tagOf(identifierType) ?? EventTagDescriptor.NameFor(identifierType);
            var member = binding.TryGetProperty("id", out var id) ? parameterName(id) : null;

            addRead(slice, "Model", entityName, tag, member);
            describeEntity(slice, entityName, entity, identifierType);
        }

        /// <summary>An entity as a stub: its identity, then one member per property, typed by the projection behind it.</summary>
        private void describeEntity(CuratedSlice slice, string entityName, JsonElement entity, string identifierType)
        {
            var element = elementOf(slice, entityName);
            if (element.Fields.Count > 0) return;

            var identityName = text(entity, "identifierName") is { Length: > 0 } named ? named : char.ToLowerInvariant(entityName[0]) + entityName[1..] + "Id";
            var identity = field(identityName, identifierType, optional: false, list: false);
            identity.Identity = true;
            identity.Tag ??= EventTagDescriptor.NameFor(identifierType);
            declare(element, identity);
            if (!element.Identities.Contains(identityName)) element.Identities.Add(identityName);

            foreach (var property in items(entity, "properties"))
            {
                var name = text(property, "name");
                var projection = _projections.GetValueOrDefault(text(property, "projection"));
                if (name.Length == 0) continue;

                declare(element, field(name, text(projection, "valueType") is { Length: > 0 } v ? v : "string", optional: false, flag(projection, "isList")));
            }
        }

        /// <summary>A projection read, by each tag it is given; a projection declaring no tags is an untagged one.</summary>
        private void projectionRead(CuratedSlice slice, string projectionName, JsonElement read)
        {
            if (projectionName.Length == 0) return;
            var projection = _projections.GetValueOrDefault(projectionName);
            var declaredTags = items(projection, "tags").ToDictionary(x => text(x, "name"), x => text(x, "tagType"), StringComparer.Ordinal);

            if (declaredTags.Count == 0)
            {
                if (!slice.UntaggedProjections.Contains(projectionName)) slice.UntaggedProjections.Add(projectionName);
                return;
            }

            var given = read.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object
                ? tags.EnumerateObject().ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            foreach (var (tagName, tagType) in declaredTags)
            {
                var tag = tagOf(tagType) ?? EventTagDescriptor.NameFor(tagType.Length > 0 ? tagType : tagName);
                var member = given.TryGetValue(tagName, out var operand) ? parameterName(operand) : null;
                addRead(slice, "Projection", projectionName, tag, member);
            }

            if (!slice.ReadsFrom.Contains(projectionName)) slice.ReadsFrom.Add(projectionName);
        }

        private static void addRead(CuratedSlice slice, string kind, string type, string tag, string? member)
        {
            if (slice.Reads.Any(x => x.Kind == kind && x.Type == type && x.Tag == tag && x.Member == member)) return;
            slice.Reads.Add(new CuratedRead { Kind = kind, Type = type, Tag = tag, Member = member });
        }

        /// <summary>The command parameter an operand names (<c>{ parameterName: "courseId" }</c>); null for a literal, an alias, another read, or a fan-out.</summary>
        private static string? parameterName(JsonElement operand)
            => operand.ValueKind == JsonValueKind.Object && text(operand, "parameterName") is { Length: > 0 } name ? name : null;

        /// <summary>Every object in the command that reads a projection in place: <c>{ projection, tags }</c> anywhere but in the boundary.</summary>
        private static IEnumerable<JsonElement> inlineReads(JsonElement command)
        {
            foreach (var property in command.EnumerateObject())
            {
                if (property.Name is "boundary" or "properties" or "publishes") continue;
                foreach (var read in readsIn(property.Value)) yield return read;
            }

            // An emission's parameter values and guards may read a projection too
            foreach (var emission in items(command, "publishes"))
            {
                foreach (var property in emission.EnumerateObject().Where(x => x.Name != "name"))
                {
                    foreach (var read in readsIn(property.Value)) yield return read;
                }
            }
        }

        private static IEnumerable<JsonElement> readsIn(JsonElement node)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    if (node.TryGetProperty("projection", out var p) && p.ValueKind == JsonValueKind.String && !node.TryGetProperty("alias", out _))
                    {
                        yield return node;
                    }

                    foreach (var property in node.EnumerateObject())
                    {
                        foreach (var read in readsIn(property.Value)) yield return read;
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray())
                    {
                        foreach (var read in readsIn(item)) yield return read;
                    }

                    break;
            }
        }

        // ---- projections -------------------------------------------------------------------------

        /// <summary>A projection is a View slice: it folds the events its handlers name into one value per tag.</summary>
        private void projectionSlice(JsonElement projection)
        {
            var name = text(projection, "name");
            if (name.Length == 0 || _slices.ContainsKey(name)) return;

            var consumed = items(projection, "handlers").Select(x => text(x, "event")).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            var slice = new CuratedSlice
            {
                Name = name,
                Pattern = "View",
                Dcb = true,
                ReadModels = [name],
                ConsumedEvents = consumed,
                Notes = $"From the dcb.events model '{text(_root, "name")}'" + (projection.TryGetProperty("derived", out _) ? ", a derived projection." : projection.TryGetProperty("script", out _) ? ", a scripted projection." : "."),
            };

            // The read model: one member per tag it is read by, then the value it folds to
            var element = elementOf(slice, name);
            foreach (var tag in items(projection, "tags"))
            {
                var tagName = text(tag, "name");
                var tagType = text(tag, "tagType");
                if (tagName.Length == 0) continue;

                var declared = field(tagName, tagType, optional: false, list: false);
                declared.Identity = true;
                declared.Tag ??= EventTagDescriptor.NameFor(tagType.Length > 0 ? tagType : tagName);
                declare(element, declared);
                if (!element.Identities.Contains(tagName)) element.Identities.Add(tagName);
            }

            var valueType = text(projection, "valueType");
            declare(element, field("value", valueType.Length > 0 ? valueType : "string", optional: false, flag(projection, "isList")));

            foreach (var eventName in consumed)
            {
                if (_events.TryGetValue(eventName, out var @event) && !_model.Slices.Any(s => s.Elements.ContainsKey(eventName))) describeEvent(slice, @event);
            }

            _model.Slices.Add(slice);
            _slices[name] = slice;
            _report.Add($"projection '{name}': View slice over {consumed.Count} event(s).");
        }

        // ---- scenarios ---------------------------------------------------------------------------

        private void commandScenario(JsonElement scenario)
        {
            var command = text(scenario, "command");
            if (!_slices.TryGetValue(command, out var slice))
            {
                _report.Add($"⚠ scenario '{text(scenario, "name")}' exercises command '{command}', which the model does not define — not attached.");
                return;
            }

            var given = items(scenario, "given").Select(givenEvent).ToList();
            var arguments = scenario.TryGetProperty("when", out var when) && when.TryGetProperty("arguments", out var args) ? values(args) : new Dictionary<string, string>(StringComparer.Ordinal);
            var then = scenario.GetProperty("then");
            var rejected = text(then, "outcome").Equals("rejected", StringComparison.OrdinalIgnoreCase);
            var emitted = items(then, "events").Select(x => (Event: text(x, "type"), Values: x.TryGetProperty("data", out var data) ? values(data) : new Dictionary<string, string>(StringComparer.Ordinal))).ToList();
            var rejection = text(then, "rejection");

            var outcomes = new List<ScenarioOutcome>();
            if (rejected) outcomes.Add(new Refusal(rejection, RefusalKind.Refused, []));
            else if (emitted.Count == 0) outcomes.Add(new NothingEmitted());
            else outcomes.AddRange(emitted.Select(x => new EventEmitted(x.Event, x.Values) { Tags = tagValues(x.Event, x.Values) }));

            var title = text(scenario, "name");
            if (title.Length == 0)
            {
                title = rejected ? $"rejected: {rejection}" : emitted.Count == 0 ? "nothing happens" : $"publishes {string.Join(", ", emitted.Select(x => x.Event))}";
            }

            var model = new ScenarioModel(title)
            {
                SourceId = text(scenario, "id") is { Length: > 0 } id ? id : null,
                GivenEvents = given,
                Act = new MessageReceived(command, arguments),
                Outcomes = outcomes,
            };

            attach(slice, model, then: rejected
                ? [new CuratedThen { ValidationFails = rejection }]
                : emitted.Select(x => new CuratedThen { Event = x.Event, With = new Dictionary<string, string>(x.Values, StringComparer.Ordinal) }).ToList(),
                when: new CuratedWhen { Command = command, With = new Dictionary<string, string>(arguments, StringComparer.Ordinal) });
        }

        private void projectionScenario(JsonElement scenario)
        {
            var projection = text(scenario, "projection");
            if (!_slices.TryGetValue(projection, out var slice))
            {
                _report.Add($"⚠ projection scenario '{text(scenario, "name")}' is about '{projection}', which the model does not define — not attached.");
                return;
            }

            var given = items(scenario, "given").Select(givenEvent).ToList();
            var expected = new Dictionary<string, string>(StringComparer.Ordinal);
            if (scenario.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            {
                foreach (var tag in tags.EnumerateObject())
                {
                    expected[tag.Name] = tag.Value.TryGetProperty("tagValue", out var value) ? valueText(value) : valueText(tag.Value);
                }
            }

            var then = scenario.TryGetProperty("then", out var t) ? valueText(t) : "";
            expected["value"] = then;

            var title = text(scenario, "name");
            if (title.Length == 0) title = $"folds to {(then.Length == 0 ? "nothing" : then)}";

            var model = new ScenarioModel(title)
            {
                SourceId = text(scenario, "id") is { Length: > 0 } id ? id : null,
                GivenEvents = given,
                Outcomes = [new ReadModelOutcome(projection, ReadModelShape.Partial, MatchMode.Subset, expected)],
            };

            attach(slice, model, then: [new CuratedThen { ReadModel = projection, Contains = new Dictionary<string, string>(expected, StringComparer.Ordinal) }], when: null);
        }

        private void attach(CuratedSlice slice, ScenarioModel model, List<CuratedThen> then, CuratedWhen? when)
        {
            slice.Specifications ??= new CuratedSpecifications();
            var scenarios = slice.Specifications.Scenarios;

            // The identity must stay unique on the slice: an unnamed scenario is named for its outcome, which repeats
            var name = ProjectedSpecNaming.ExampleScenarioTitle(model.Name);
            var unique = name;
            for (var n = 2; scenarios.Any(x => x.Name == unique); n++) unique = $"{name} {n}";

            var bound = model with { Name = unique, Slice = slice.Name, Chapter = slice.Chapter, Pattern = slice.Pattern };
            scenarios.Add(new CuratedScenario
            {
                Name = unique,
                Model = bound,
                Source = bound.ToEmlangTest(),
                Given = bound.GivenEvents.Select(x => new CuratedGiven { Event = x.Event, With = new Dictionary<string, string>(x.Values, StringComparer.Ordinal) }).ToList(),
                When = when,
                Then = then,
            });
        }

        private GivenEvent givenEvent(JsonElement item)
        {
            var name = text(item, "event");
            var data = item.TryGetProperty("data", out var d) ? values(d) : new Dictionary<string, string>(StringComparer.Ordinal);
            return new GivenEvent(name, data) { Tags = tagValues(name, data) };
        }

        /// <summary>The tag values an event instance carries: the value of each property whose type is a tag, under the tag's name.</summary>
        private IReadOnlyDictionary<string, string> tagValues(string eventName, IReadOnlyDictionary<string, string> data)
        {
            if (!_events.TryGetValue(eventName, out var @event)) return ScenarioModel.None;

            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in items(@event, "properties"))
            {
                var tag = tagOf(text(property, "propertyType"));
                if (tag is not null && data.TryGetValue(text(property, "name"), out var value)) tags[tag] = value;
            }

            return tags.Count == 0 ? ScenarioModel.None : tags;
        }

        private static Dictionary<string, string> values(JsonElement data)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (data.ValueKind != JsonValueKind.Object) return result;
            foreach (var property in data.EnumerateObject()) result[property.Name] = valueText(property.Value);
            return result;
        }

        /// <summary>A JSON value as the text a specification cell holds: <c>NULL</c> for null, items joined for a list, the raw JSON for an object.</summary>
        private static string valueText(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "NULL",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.TryGetDecimal(out var number) ? number.ToString(CultureInfo.InvariantCulture) : value.GetRawText(),
            JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(valueText)),
            _ => value.GetRawText(),
        };

        // ---- json --------------------------------------------------------------------------------

        private static IEnumerable<JsonElement> items(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) yield break;
            if (value.ValueKind != JsonValueKind.Array) yield break;
            foreach (var item in value.EnumerateArray()) yield return item;
        }

        private static string text(JsonElement element, string property)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        private static bool flag(JsonElement element, string property)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    }
}
