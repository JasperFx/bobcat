using Bobcat.EventModel.Emlang;
using Bobcat.EventModel.Scenarios;

namespace Bobcat.EventModel.Esdm;

/// <summary>
/// Reads ESDM documents (the native web's <c>.esdm.yaml</c>, <c>schema.esdm.io/core/v1</c> and the
/// <c>given-when-then/v1</c> extension, bobcat#850) into the imported model, read-only: no writer, and nothing an
/// ESDM model cannot say is invented.
/// </summary>
/// <remarks>
/// <para>
/// ESDM is a DDD / CQRS / event-sourcing model of eighteen kinds with no slices, lanes or chapters. The lowering:
/// a <c>command</c> is a Command slice against its consistency unit — <c>.Against&lt;Aggregate&gt;()</c> for an
/// aggregate-scoped one, and for a DCB-scoped one a read of the boundary by each of its <c>identifiedBy</c> tags
/// (<see cref="CuratedSlice.Dcb"/>); a <c>dynamic-consistency-boundary</c> is a View slice folding the events it
/// <c>consults</c>, so JasperFx derives the boundary query from the read; a <c>read-model</c> is a View slice over its
/// <c>projections</c>; a <c>query</c> is a Query slice reading the read model; a <c>policy</c>, an <c>event-handler</c>
/// and a <c>process-manager</c> are Automation slices on the events they handle, publishing the commands they emit.
/// </para>
/// <para>
/// The bounded context is the slice's chapter and the domain its domain. Aggregate and DCB <c>invariants</c> and a
/// command's <c>constraints</c> are the reasons its commands refuse (their <c>rule</c> text), and a <c>feature</c>
/// scenario's <c>rejection.invariant</c> is the refusal naming that invariant. Everything else — subdomains,
/// context mappings, entities, value objects, domain services, external systems — is counted in the report and
/// lowered to nothing: the Event Model has no element for it.
/// </para>
/// </remarks>
public static class EsdmReader
{
    /// <summary>The <c>apiVersion</c> prefix every ESDM document carries.</summary>
    public const string ApiVersionPrefix = "schema.esdm.io/";

    /// <summary>Whether one parsed YAML document is an ESDM document.</summary>
    public static bool IsEsdm(Dictionary<object, object> document)
        => document.TryGetValue("apiVersion", out var version) && version?.ToString()?.StartsWith(ApiVersionPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// Lower every document in <paramref name="yaml"/> (one file, <c>---</c>-separated) to the imported model.
    /// <paramref name="modelName"/> null takes the domain's name.
    /// </summary>
    /// <exception cref="EmlangFormatException">Not YAML, or no ESDM document in it.</exception>
    public static EmlangImportResult ToCurated(string yaml, string? modelName = null, string? @namespace = null)
        => ToCurated([yaml], modelName, @namespace);

    /// <summary>Lower the documents of several files (a model split one kind per file) as one model.</summary>
    public static EmlangImportResult ToCurated(IEnumerable<string> files, string? modelName = null, string? @namespace = null)
    {
        var documents = new List<Dictionary<object, object>>();
        foreach (var yaml in files)
        {
            try
            {
                documents.AddRange(EmlangReader.Documents(yaml));
            }
            catch (YamlDotNet.Core.YamlException e)
            {
                throw new EmlangFormatException($"not parseable as YAML: {e.Message}");
            }
        }

        var esdm = documents.Where(IsEsdm).ToList();
        if (esdm.Count == 0) throw new EmlangFormatException("no ESDM document: every document has an apiVersion under schema.esdm.io/");

        return new Lowering(esdm, modelName, @namespace).Run();
    }

    private sealed class Lowering
    {
        private readonly List<Dictionary<object, object>> _documents;
        private readonly ImportedEventModel _model;
        private readonly List<string> _report = [];
        private readonly List<ImportCallout> _callouts = [];
        private readonly Dictionary<string, CuratedSlice> _slices = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<object, object>> _aggregates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<object, object>> _boundaries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<object, object>> _events = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _actorKinds = new(StringComparer.Ordinal);

        public Lowering(List<Dictionary<object, object>> documents, string? modelName, string? @namespace)
        {
            _documents = documents;
            var domain = documents.FirstOrDefault(x => kind(x) == "domain") is { } d ? str(d, "name") : documents.Select(x => str(map(x, "scope"), "domain")).FirstOrDefault(x => x.Length > 0) ?? "";
            var name = modelName ?? EmlangImport.PascalName(domain);
            if (name.Length == 0) name = "EsdmModel";
            _model = new ImportedEventModel { Schema = 1, Model = name, Namespace = @namespace };
        }

        public EmlangImportResult Run()
        {
            foreach (var document in _documents)
            {
                switch (kind(document))
                {
                    case "aggregate": _aggregates[str(document, "name")] = document; break;
                    case "dynamic-consistency-boundary": _boundaries[str(document, "name")] = document; break;
                    case "event": _events[str(document, "name")] = document; break;
                    case "actor": _actorKinds[str(document, "name")] = str(document, "type"); break;
                }
            }

            // Events first, so every command knows the stream its events are stored on
            foreach (var (name, @event) in _events)
            {
                var aggregate = str(map(@event, "scope"), "aggregate");
                if (aggregate.Length > 0) _model.EventStreams[Type(name)] = Type(aggregate);
            }

            foreach (var boundary in _boundaries.Values) boundarySlice(boundary);
            foreach (var document in _documents.Where(x => kind(x) == "command")) commandSlice(document);
            foreach (var document in _documents.Where(x => kind(x) == "read-model")) readModelSlice(document);
            foreach (var document in _documents.Where(x => kind(x) == "query")) querySlice(document);
            foreach (var document in _documents.Where(x => kind(x) is "policy" or "event-handler" or "process-manager")) automationSlice(document);

            // An event nothing publishes still needs a stub: the first slice consuming it carries its fields
            foreach (var (name, @event) in _events)
            {
                var type = Type(name);
                if (_model.Slices.Any(s => s.Events.Contains(type) || s.Elements.ContainsKey(type))) continue;
                var consumer = _model.Slices.FirstOrDefault(s => s.ConsumedEvents.Contains(type));
                if (consumer is not null) describe(consumer, type, map(@event, "data"), str(@event, "description"));
            }

            foreach (var document in _documents.Where(x => kind(x) == "feature")) feature(document);

            var ignored = _documents.Select(kind).Where(x => x is "domain" or "subdomain" or "bounded-context" or "context-mapping" or "entity" or "value-object" or "domain-service" or "external-system" or "actor")
                .GroupBy(x => x, StringComparer.Ordinal).Select(x => $"{x.Count()} {x.Key}").ToList();
            _report.Add($"ESDM model '{_model.Model}': {_slices.Values.Count(x => x.Pattern == "Command")} command slice(s), "
                        + $"{_slices.Values.Count(x => x.Pattern == "View")} view(s), {_slices.Values.Count(x => x.Pattern == "Query")} query slice(s), "
                        + $"{_slices.Values.Count(x => x.Pattern == "Automation")} automation(s), "
                        + $"{_model.Slices.Sum(x => x.Specifications?.Scenarios.Count ?? 0)} scenario(s)."
                        + (ignored.Count > 0 ? $" Not lowered (no Event Model element for them): {string.Join(", ", ignored)}." : ""));

            return new EmlangImportResult(_model, _report) { Callouts = _callouts };
        }

        // ---- slices -------------------------------------------------------------------------------

        private CuratedSlice open(string name, string pattern, Dictionary<object, object> document)
        {
            var scope = map(document, "scope");
            var slice = new CuratedSlice
            {
                Name = Type(name),
                Pattern = pattern,
                Domain = str(scope, "domain") is { Length: > 0 } domain ? domain : null,
                Chapter = str(scope, "boundedContext") is { Length: > 0 } context ? context : null,
                Description = str(document, "description") is { Length: > 0 } description ? description : null,
                Notes = $"From the ESDM {kind(document)} '{name}'.",
            };

            _model.Slices.Add(slice);
            _slices[slice.Name] = slice;
            return slice;
        }

        /// <summary>
        /// A DCB is a View slice over the events it consults: the state the decision reads, folded from those events,
        /// one document per identifier. Its commands read it by tag, and the boundary is derived from the read.
        /// </summary>
        private void boundarySlice(Dictionary<object, object> boundary)
        {
            var name = str(boundary, "name");
            var slice = open(name, "View", boundary);
            slice.Dcb = true;
            slice.ReadModels.Add(slice.Name);
            slice.ConsumedEvents.AddRange(list(boundary, "consults").Select(x => Type(str(x, "event"))).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal));

            var element = elementOf(slice, slice.Name);
            foreach (var identifier in list(boundary, "identifiedBy"))
            {
                var tag = Tag(str(identifier, "name"));
                if (tag.Length == 0) continue;
                _model.Tags.TryAdd(tag, null);
                declare(element, new CuratedField { Name = tag, Type = "string", Identity = true, Tag = tag });
                if (!element.Identities.Contains(tag)) element.Identities.Add(tag);
            }

            foreach (var consult in list(boundary, "consults"))
            {
                var criteria = str(consult, "criteria");
                if (criteria.Length > 0) slice.Notes += $"\nConsults {Type(str(consult, "event"))}: {criteria}";
            }

            _report.Add($"dynamic-consistency-boundary '{name}': View slice over {slice.ConsumedEvents.Count} consulted event(s), read by {string.Join(", ", _model.Tags.Keys.Where(t => element.Identities.Contains(t)))}.");
        }

        private void commandSlice(Dictionary<object, object> command)
        {
            var name = str(command, "name");
            var scope = map(command, "scope");
            var slice = open(name, "Command", command);
            slice.Command = slice.Name;
            describe(slice, slice.Name, map(command, "data"), str(command, "description"));

            foreach (var published in strings(command, "publishes").Select(Type).Where(x => x.Length > 0))
            {
                if (!slice.Events.Contains(published)) slice.Events.Add(published);
                if (_events.TryGetValue(kebabOf(published), out var @event)) describe(slice, published, map(@event, "data"), str(@event, "description"));
            }

            var aggregate = str(scope, "aggregate");
            var boundary = str(scope, "dynamicConsistencyBoundary");
            if (aggregate.Length > 0)
            {
                slice.Aggregates.Add(Type(aggregate));
                describeAggregate(slice, aggregate);
                refuses(slice, _aggregates.GetValueOrDefault(aggregate), "invariants");

                // The command's and its events' member named like the aggregate's identity IS the identity, so no Id is invented
                if (str(map(_aggregates.GetValueOrDefault(aggregate), "identifiedBy"), "field") is { Length: > 0 } identity)
                {
                    foreach (var typeName in slice.Events.Prepend(slice.Name))
                    {
                        if (!slice.Elements.TryGetValue(typeName, out var element) || !element.DeclaredFields.TryGetValue(identity, out var field)) continue;
                        field.Identity = true;
                        if (!element.Identities.Contains(identity)) element.Identities.Add(identity);
                    }
                }

                // ESDM says which aggregate, never whether the command starts its stream
                var message = $"⚠ slice '{slice.Name}': decides against {Type(aggregate)}; ESDM does not say whether it starts the stream, so it is assumed to append to one.";
                _report.Add(message);
                slice.Callouts.Add(message);
                _callouts.Add(new ImportCallout(slice.Name, ImportCalloutCodes.StreamStartUnknown, [Type(aggregate)], message,
                    $"--starts-stream {slice.Name}={Type(aggregate)}"));
            }
            else if (boundary.Length > 0 && _boundaries.TryGetValue(boundary, out var dcb))
            {
                slice.Dcb = true;
                var boundaryType = Type(boundary);
                foreach (var identifier in list(dcb, "identifiedBy"))
                {
                    var tag = Tag(str(identifier, "name"));
                    if (tag.Length == 0) continue;

                    var member = str(identifier, "source") == "command-payload" ? str(identifier, "field") : null;
                    slice.Reads.Add(new CuratedRead { Kind = "Projection", Type = boundaryType, Tag = tag, Member = member is { Length: > 0 } ? member : null });

                    // The command member that supplies the tag knows it, and the tag's type is that member's
                    if (member is { Length: > 0 } && slice.Elements[slice.Name].DeclaredFields.TryGetValue(member, out var field))
                    {
                        field.Tag = tag;
                        if (_model.Tags.GetValueOrDefault(tag) is null) _model.Tags[tag] = field.Type;
                    }
                }

                if (!slice.ReadsFrom.Contains(boundaryType)) slice.ReadsFrom.Add(boundaryType);
                refuses(slice, dcb, "invariants");
            }
            else if (boundary.Length > 0)
            {
                _report.Add($"⚠ command '{name}' is scoped to dynamic-consistency-boundary '{boundary}', which no document declares.");
            }

            refuses(slice, command, "constraints");
            trigger(slice, command);

            _report.Add($"command '{name}': Command slice" + (aggregate.Length > 0 ? $" against {Type(aggregate)}" : boundary.Length > 0 ? $" in boundary {Type(boundary)}" : "")
                        + $", {slice.Events.Count} event(s), {slice.RejectionReasons.Count} rejection reason(s).");
        }

        private void describeAggregate(CuratedSlice slice, string aggregateName)
        {
            if (!_aggregates.TryGetValue(aggregateName, out var aggregate)) return;
            var type = Type(aggregateName);
            if (_model.Slices.Any(s => s.Elements.ContainsKey(type))) return;

            describe(slice, type, map(aggregate, "state"), str(aggregate, "description"));
            var identified = map(aggregate, "identifiedBy");
            if (str(identified, "source") == "state" && str(identified, "field") is { Length: > 0 } field)
            {
                var element = elementOf(slice, type);
                if (!element.Identities.Contains(field)) element.Identities.Add(field);
                if (element.DeclaredFields.TryGetValue(field, out var declared)) declared.Identity = true;
            }
        }

        /// <summary>The reasons a command can refuse with: the <c>rule</c> text of each named rule in <paramref name="property"/>.</summary>
        private static void refuses(CuratedSlice slice, Dictionary<object, object>? unit, string property)
        {
            if (unit is null) return;
            foreach (var rule in list(unit, property))
            {
                var reason = reasonOf(rule);
                if (reason.Length > 0 && !slice.RejectionReasons.Contains(reason)) slice.RejectionReasons.Add(reason);
            }
        }

        /// <summary>A named rule's reason: its <c>rule</c> text, else its name.</summary>
        private static string reasonOf(Dictionary<object, object> rule)
            => str(rule, "rule") is { Length: > 0 } text ? text : str(rule, "name");

        private void trigger(CuratedSlice slice, Dictionary<object, object> command)
        {
            var actor = strings(command, "actors").FirstOrDefault(x => x.Length > 0);
            if (actor is null) return;

            var kind = _actorKinds.GetValueOrDefault(actor) == "system" ? "External" : "Human";
            slice.Trigger = new CuratedTrigger { Kind = kind, Label = actor };
        }

        private void readModelSlice(Dictionary<object, object> readModel)
        {
            var name = str(readModel, "name");
            var slice = open(name, "View", readModel);
            slice.ReadModels.Add(slice.Name);
            slice.ConsumedEvents.AddRange(list(readModel, "projections").Select(x => Type(str(x, "event"))).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal));

            // A tabular read model's schema is an array of rows: the row is the document
            var schema = map(readModel, "schema");
            var shape = str(schema, "type") == "array" ? map(schema, "items") : schema;
            describe(slice, slice.Name, shape, str(readModel, "description"));
            if (str(readModel, "paradigm") is { Length: > 0 } paradigm) slice.Notes += $" Paradigm: {paradigm}.";

            foreach (var projection in list(readModel, "projections"))
            {
                var rule = str(projection, "rule");
                if (rule.Length > 0) slice.Notes += $"\nOn {Type(str(projection, "event"))}: {rule}";
            }

            _report.Add($"read-model '{name}': View slice over {slice.ConsumedEvents.Count} event(s).");
        }

        private void querySlice(Dictionary<object, object> query)
        {
            var name = str(query, "name");
            var slice = open(name, "Query", query);
            var readModel = Type(str(query, "readModel"));
            if (readModel.Length > 0) slice.ReadsFrom.Add(readModel);
            trigger(slice, query);
            refuses(slice, query, "constraints");

            _report.Add($"query '{name}': Query slice reading {readModel}.");
        }

        /// <summary>A policy, an event handler or a process manager: an automation on the events it handles, publishing the commands it emits.</summary>
        private void automationSlice(Dictionary<object, object> automation)
        {
            var name = str(automation, "name");
            var slice = open(name, "Automation", automation);
            slice.Trigger = new CuratedTrigger { Kind = "MessageHandler" };

            var handled = list(automation, "handles").Concat(list(automation, "startsWhen"))
                .Concat(list(automation, "reactions").Select(x => map(x, "when")).Where(x => str(x, "event").Length > 0))
                .Select(x => Type(str(x, "event")))
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal);
            slice.ConsumedEvents.AddRange(handled);

            var emitted = list(automation, "emits").Concat(list(automation, "reactions").SelectMany(x => list(x, "emits")))
                .Select(x => Type(str(x, "command")))
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal);
            slice.Messages.AddRange(emitted);

            foreach (var effect in list(automation, "sideEffects"))
            {
                var system = str(effect, "externalSystem");
                if (system.Length > 0 && slice.ExternalSystems.All(x => x.Name != system))
                {
                    slice.ExternalSystems.Add(new CuratedExternalSystem { Name = system, Direction = "Outbound" });
                }
            }

            var timers = list(automation, "timers").Select(x => str(x, "name")).Where(x => x.Length > 0).ToList();
            if (timers.Count > 0) slice.Notes += $" Timers: {string.Join(", ", timers)}.";
            if (str(automation, "deliveryGuarantee") is { Length: > 0 } guarantee) slice.Notes += $" Delivery: {guarantee}.";

            var kindName = kind(automation);
            _report.Add($"{kindName} '{name}': Automation slice on {slice.ConsumedEvents.Count} event(s), emitting {slice.Messages.Count} command(s).");
        }

        // ---- fields -------------------------------------------------------------------------------

        private CuratedElement elementOf(CuratedSlice slice, string typeName)
        {
            if (!slice.Elements.TryGetValue(typeName, out var element))
            {
                element = new CuratedElement();
                slice.Elements[typeName] = element;
            }

            return element;
        }

        private static void declare(CuratedElement element, CuratedField declared)
        {
            element.Fields.TryAdd(declared.Name, declared.Many ? CuratedFieldTypes.StringList : declared.Type);
            element.DeclaredFields.TryAdd(declared.Name, declared);
        }

        /// <summary>The members of a type from its JSON Schema object: one per property, optional unless <c>required</c> names it.</summary>
        private void describe(CuratedSlice slice, string typeName, Dictionary<object, object>? schema, string? description)
        {
            var element = elementOf(slice, typeName);
            element.Description ??= description is { Length: > 0 } ? description : null;
            if (schema is null) return;

            var required = strings(schema, "required").ToHashSet(StringComparer.Ordinal);
            foreach (var (key, value) in map(schema, "properties") ?? [])
            {
                var name = key.ToString() ?? "";
                if (name.Length == 0) continue;
                declare(element, field(name, value as Dictionary<object, object>, optional: !required.Contains(name)));
            }
        }

        /// <summary>A JSON Schema property as a field: the primitive by type and format, a list from an array, a nested record from an object.</summary>
        private CuratedField field(string name, Dictionary<object, object>? schema, bool optional)
        {
            var type = str(schema, "type");
            var format = str(schema, "format");

            if (type == "array")
            {
                var items = field(name, map(schema, "items"), optional: false);
                items.Many = true;
                items.Optional = optional;
                return items;
            }

            if (type == "object" && map(schema, "properties") is { Count: > 0 } nested)
            {
                var required = strings(schema, "required").ToHashSet(StringComparer.Ordinal);
                return new CuratedField
                {
                    Name = name,
                    Type = CSharpModelWriter.Identifiers.Sanitize(EmlangImport.PascalName(name)),
                    Optional = optional,
                    Description = str(schema, "description") is { Length: > 0 } text ? text : null,
                    Subfields = nested.Select(x => field(x.Key.ToString() ?? "", x.Value as Dictionary<object, object>, !required.Contains(x.Key.ToString() ?? ""))).Where(x => x.Name.Length > 0).ToList(),
                };
            }

            return new CuratedField
            {
                Name = name,
                Type = (type, format) switch
                {
                    ("string", "uuid") => "Guid",
                    ("string", "date-time") => "DateTimeOffset",
                    ("string", "date") => "DateOnly",
                    ("integer", _) => "int",
                    ("number", _) => "decimal",
                    ("boolean", _) => "bool",
                    _ => "string",
                },
                Optional = optional,
                Description = str(schema, "description") is { Length: > 0 } d ? d : null,
            };
        }

        // ---- features -----------------------------------------------------------------------------

        private void feature(Dictionary<object, object> feature)
        {
            var scope = map(feature, "scope");
            var featureName = str(feature, "name");

            foreach (var scenario in list(feature, "scenarios"))
            {
                var when = map(scenario, "when");
                var then = map(scenario, "then");
                var given = list(scenario, "given").Select(givenEvent).ToList();
                var title = str(scenario, "name") is { Length: > 0 } named ? named.Replace('-', ' ') : "scenario";

                CuratedSlice? slice;
                ScenarioModel model;
                CuratedWhen? curatedWhen = null;
                var curatedThen = new List<CuratedThen>();

                if (str(scope, "processManager") is { Length: > 0 } pm)
                {
                    slice = _slices.GetValueOrDefault(Type(pm));
                    ScenarioAct? act = str(when, "timer") is { Length: > 0 } timer
                        ? new TimeAdvances(timer)
                        : str(when, "event") is { Length: > 0 } evt ? new MessageReceived(Type(evt), values(map(when, "data"))) : null;

                    var outcomes = new List<ScenarioOutcome>();
                    foreach (var emit in list(then, "emits"))
                    {
                        var command = Type(str(emit, "command"));
                        if (command.Length > 0) outcomes.Add(new MessageCaused(command, values(map(emit, "data")), Scheduled: false));
                    }

                    foreach (var set in strings(then, "setTimers").Where(x => x.Length > 0))
                    {
                        outcomes.Add(new MessageCaused(set, ScenarioModel.None, Scheduled: true));
                    }

                    if (map(then, "state") is { Count: > 0 } state && slice is not null)
                    {
                        outcomes.Add(new ReadModelOutcome(slice.Name, ReadModelShape.Partial, MatchMode.Subset, values(state)));
                    }

                    model = new ScenarioModel(title)
                    {
                        GivenEvents = given,
                        Act = act,
                        Outcomes = outcomes,
                        Note = then.TryGetValue("ended", out var ended) && ended?.ToString() == "true" ? "The process manager ends here." : null,
                    };
                }
                else if (str(scope, "readModel") is { Length: > 0 } readModel)
                {
                    slice = _slices.GetValueOrDefault(Type(readModel));
                    var result = then.TryGetValue("result", out var r) ? r : then.GetValueOrDefault("readModel");
                    var rows = result as List<object>;
                    var outcome = rows is not null
                        ? new ReadModelOutcome(Type(readModel), ReadModelShape.Rows, MatchMode.Exact, ScenarioModel.None)
                        {
                            Rows = rows.OfType<Dictionary<object, object>>().Select(values).Select(x => (IReadOnlyDictionary<string, string>)x).ToList(),
                        }
                        : new ReadModelOutcome(Type(readModel), ReadModelShape.Partial, MatchMode.Subset, values(result as Dictionary<object, object>));

                    model = new ScenarioModel(title)
                    {
                        GivenEvents = given,
                        Act = str(when, "query") is { Length: > 0 } query ? new ReadModelQueried(Type(readModel), values(map(when, "parameters"))) : null,
                        Outcomes = [outcome],
                    };
                    curatedThen.Add(new CuratedThen { ReadModel = Type(readModel), Contains = new Dictionary<string, string>(outcome.Values, StringComparer.Ordinal) });
                }
                else
                {
                    // An aggregate's or a DCB's commands: Given events, When a command, Then events or a rejection
                    var command = Type(str(when, "command"));
                    slice = _slices.GetValueOrDefault(command);
                    var arguments = values(map(when, "data"));
                    var unit = str(scope, "aggregate") is { Length: > 0 } aggregate ? _aggregates.GetValueOrDefault(aggregate)
                        : str(scope, "dynamicConsistencyBoundary") is { Length: > 0 } dcb ? _boundaries.GetValueOrDefault(dcb) : null;

                    var outcomes = new List<ScenarioOutcome>();
                    string? rule = null;
                    if (map(then, "rejection") is { } rejection)
                    {
                        var invariant = str(rejection, "invariant");
                        var reason = invariantReason(unit, slice, invariant);
                        rule = invariant;
                        outcomes.Add(new Refusal(reason, RefusalKind.Refused, []));
                        curatedThen.Add(new CuratedThen { ValidationFails = reason });
                    }
                    else
                    {
                        foreach (var emitted in list(then, "events"))
                        {
                            var eventName = Type(str(emitted, "event"));
                            var data = values(map(emitted, "data"));
                            outcomes.Add(new EventEmitted(eventName, data) { Stream = _model.EventStreams.GetValueOrDefault(eventName) });
                            curatedThen.Add(new CuratedThen { Event = eventName, With = new Dictionary<string, string>(data, StringComparer.Ordinal) });
                        }

                        if (outcomes.Count == 0 && then.ContainsKey("events")) outcomes.Add(new NothingEmitted());
                    }

                    model = new ScenarioModel(title)
                    {
                        GivenEvents = given,
                        Act = new MessageReceived(command, arguments),
                        Outcomes = outcomes,
                        Rule = rule,
                        Actor = str(when, "actor") is { Length: > 0 } actor ? actor : null,
                    };
                    curatedWhen = new CuratedWhen { Command = command, With = new Dictionary<string, string>(arguments, StringComparer.Ordinal) };
                }

                if (slice is null)
                {
                    _report.Add($"⚠ feature '{featureName}': scenario '{title}' is about a unit the model does not define — not attached.");
                    continue;
                }

                attach(slice, model, curatedWhen, curatedThen, description: str(scenario, "description"));
            }
        }

        /// <summary>The reason a scenario's <c>rejection.invariant</c> stands for: the invariant's <c>rule</c> text on its unit, else the name itself.</summary>
        private static string invariantReason(Dictionary<object, object>? unit, CuratedSlice? slice, string invariant)
        {
            var named = list(unit, "invariants").FirstOrDefault(x => str(x, "name") == invariant);
            var reason = named is not null ? reasonOf(named) : invariant;
            if (slice is not null && reason.Length > 0 && !slice.RejectionReasons.Contains(reason)) slice.RejectionReasons.Add(reason);
            return reason;
        }

        private GivenEvent givenEvent(Dictionary<object, object> item)
        {
            var name = Type(str(item, "event"));
            var data = values(map(item, "data"));
            var stream = str(item, "aggregate") is { Length: > 0 } aggregate ? Type(aggregate) : _model.EventStreams.GetValueOrDefault(name);
            return new GivenEvent(name, data) { Stream = stream };
        }

        private void attach(CuratedSlice slice, ScenarioModel model, CuratedWhen? when, List<CuratedThen> then, string description)
        {
            slice.Specifications ??= new CuratedSpecifications();
            var scenarios = slice.Specifications.Scenarios;

            var name = ProjectedSpecNaming.ExampleScenarioTitle(model.Name);
            var unique = name;
            for (var n = 2; scenarios.Any(x => x.Name == unique); n++) unique = $"{name} {n}";

            var bound = model with
            {
                Name = unique, Slice = slice.Name, Chapter = slice.Chapter, Pattern = slice.Pattern,
                Note = model.Note ?? (description.Length > 0 ? description : null),
            };
            scenarios.Add(new CuratedScenario
            {
                Name = unique,
                Model = bound,
                Source = bound.ToEmlangTest(),
                Given = bound.GivenEvents.Select(x => new CuratedGiven { Event = x.Event, With = new Dictionary<string, string>(x.Values, StringComparer.Ordinal), Aggregate = x.Stream }).ToList(),
                When = when,
                Then = then,
            });
        }

        // ---- yaml ---------------------------------------------------------------------------------

        /// <summary>An ESDM kebab-case name as a C# type name.</summary>
        private static string Type(string name) => CSharpModelWriter.Identifiers.Sanitize(EmlangImport.PascalName(name));

        /// <summary>An ESDM kebab-case identifier name as a tag name: <c>member-id</c> is the tag <c>memberId</c>, as JasperFx names a tag type.</summary>
        private static string Tag(string name)
        {
            var pascal = Type(name);
            return pascal.Length == 0 ? "" : JasperFx.Events.EventModeling.EventTagDescriptor.NameFor(pascal);
        }

        /// <summary>The kebab-case name a type was made from, where the documents are keyed by it.</summary>
        private string kebabOf(string type) => _events.Keys.FirstOrDefault(x => Type(x) == type) ?? type;

        private static string kind(Dictionary<object, object> document) => str(document, "kind");

        private static string str(Dictionary<object, object>? map, string key)
            => map is not null && map.TryGetValue(key, out var value) && value is not null ? value.ToString() ?? "" : "";

        private static Dictionary<object, object>? map(Dictionary<object, object>? map, string key)
            => map is not null && map.TryGetValue(key, out var value) ? value as Dictionary<object, object> : null;

        private static IEnumerable<Dictionary<object, object>> list(Dictionary<object, object>? map, string key)
            => map is not null && map.TryGetValue(key, out var value) && value is List<object> items ? items.OfType<Dictionary<object, object>>() : [];

        /// <summary>A list of scalars (<c>publishes</c>, <c>actors</c>, <c>required</c>), as text.</summary>
        private static IEnumerable<string> strings(Dictionary<object, object>? map, string key)
            => map is not null && map.TryGetValue(key, out var value) && value is List<object> items ? items.Where(x => x is not null && x is not Dictionary<object, object> && x is not List<object>).Select(x => x.ToString() ?? "") : [];

        private static Dictionary<string, string> values(Dictionary<object, object>? data)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in data ?? [])
            {
                result[key.ToString() ?? ""] = value is null ? "NULL" : EmlangReader.Text(value);
            }

            return result;
        }
    }
}
