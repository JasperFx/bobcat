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
    /// The read model a View slice projects into. Its own <c>readModels:</c> when it names one,
    /// otherwise the slice's name — the same rule <see cref="ViewSliceFrame"/> emits by.
    /// </summary>
    public static string ReadModelFor(CuratedSlice slice)
        => slice.ReadModels.FirstOrDefault() ?? slice.Name;

    /// <summary>
    /// The aggregate a scenario's <c>Given no events for … / And events for …</c> steps name, or
    /// null when the slice has no write model and the model does not identify one (issue #240).
    /// </summary>
    /// <remarks>
    /// <see cref="AggregateFor"/> synthesizes <c>{Slice}Model</c> for a slice that declares no
    /// <c>aggregates:</c>, which is right for a Command or an Automation — the handler binds a
    /// <c>[WriteModel]</c> of that name, and <see cref="ScaffoldAggregates"/> emits a type for it.
    /// A View slice has no write model, so nothing emits <c>{Slice}Model</c> and the arrange step
    /// named a type that does not exist: BOBCAT011, which fails the whole spec project.
    ///
    /// And there should be no such type. The events a View scenario arranges belong to the stream
    /// the projection READS, which the model already identifies twice over — they are declared by
    /// sibling slices whose <c>aggregates:</c> say so. Resolving them is the honest answer;
    /// inventing a name is not.
    ///
    /// Arranged events that disagree — declared by slices over different aggregates — take the
    /// first and warn. A scenario arranging two streams at once is not something this grammar can
    /// express, and saying so beats picking silently.
    /// </remarks>
    public static string? ArrangeAggregateFor(CuratedModelFile model, CuratedSlice slice)
        => slice.Aggregates.FirstOrDefault()
           ?? arrangedAggregates(model, slice).FirstOrDefault()
           ?? (slice.Pattern is "Command" or "Automation" ? AggregateFor(slice) : null);

    /// <summary>Every distinct aggregate the slices declaring this one's arranged events name.</summary>
    private static List<string> arrangedAggregates(CuratedModelFile model, CuratedSlice slice)
        => (slice.Specifications?.Scenarios ?? [])
            .SelectMany(x => x.Given)
            .Select(x => x.Event)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(name => model.Slices.Where(x => x.Events.Contains(name)).SelectMany(x => x.Aggregates))
            .Distinct(StringComparer.Ordinal)
            .ToList();

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

    /// <summary>
    /// Whether this slice <b>starts</b> the stream rather than appending to an existing one —
    /// which the model already says, with no new field: every scenario bound to the slice
    /// arranges no prior events (issue #239).
    /// </summary>
    /// <remarks>
    /// <c>[WriteModel]</c> loads an existing stream; it is not the way to start one, and binding
    /// it non-nullable on a slice that creates asks the framework for an aggregate that cannot
    /// exist. The scaffolded feature said so itself — <c>Given no events for Appointment "…"</c>
    /// and nothing else — while the handler beside it demanded one.
    ///
    /// This is the same fact the aggregate scaffolder acts on when it makes the first event a
    /// <c>Create</c> rather than an <c>Apply</c>; before this, only the aggregate half derived it.
    ///
    /// It takes BOTH signals, and positive evidence for each. "No scenario arranges history" alone
    /// makes every slice of an emlang import a creating slice, because a board export whose tests
    /// declare no prior events carries no <c>given:</c> anywhere. "The act carries no id" alone
    /// catches the computed-identity request this scaffold itself teaches. Silence is not evidence
    /// either way, so a model saying nothing about the act's fields leaves the slice binding a
    /// write model — which fails loudly and accurately at dispatch, where the wrong guess in the
    /// other direction quietly creates a second stream per message forever.
    /// </remarks>
    public static bool CreatesTheStream(CuratedModelFile model, CuratedSlice slice)
    {
        // Can the act's payload identify a stream at all? [WriteModel] resolves the id out of the
        // incoming message, so a trigger carrying an upstream flow's ids and no {Aggregate}Id or
        // Id cannot bind one — and Wolverine refuses the DISPATCH rather than the body ("Unable to
        // determine an aggregate id for the parameter 'appointment'"), a framework error saying
        // nothing about the slice, where every other unfilled slice fails on its own named TODO.
        var aggregate = AggregateFor(slice);
        var actType = slice.Pattern == "Automation" ? TriggerFor(slice) : slice.Command ?? slice.Name;
        var actFields = fieldsFor(slice, actType);

        // Positive evidence only. A model that says nothing about the act's fields says nothing
        // about this either, and "we do not know" must not become "it creates a stream": a wrongly
        // bound [WriteModel] fails loudly and accurately at dispatch, where a wrongly emitted
        // StartStream would quietly create a second stream per message, forever.
        var identifiable = actFields.Count == 0
                           || actFields.Any(x => string.Equals(x.Name, "Id", StringComparison.OrdinalIgnoreCase)
                                                 || string.Equals(x.Name, aggregate + "Id", StringComparison.OrdinalIgnoreCase));

        // And the second signal, which is why one alone will not do. A request whose identity is
        // COMPUTED rather than carried has no id field either — a real shape this scaffold itself
        // teaches, `[Identity] public Guid ...Id => …` on the request record. What separates it
        // from a slice that genuinely creates is history: a computed identity addresses a stream
        // that exists, so its scenarios arrange one. A creating slice's never do.
        var arrangesHistory = (slice.Specifications?.Scenarios ?? []).Any(x => x.Given.Count > 0);

        return !identifiable && !arrangesHistory;
    }

    /// <summary>
    /// The events a View slice's projection folds, each with the Guid field a fan-out can route
    /// it by. The slice's own <c>events:</c> when it declares them, otherwise the events of the
    /// slices sharing its aggregate, otherwise its domain's, otherwise the model's.
    /// </summary>
    /// <remarks>
    /// A projection needs at least one, and not for style: Marten validates at registration that
    /// a projection has a conventional method, so a projection with none is a host that will not
    /// boot — and every scenario in the suite dies at resource startup, not just the two View
    /// slices nobody has filled in (issue #232).
    /// </remarks>
    public static IReadOnlyList<ViewSource> ViewSourcesFor(CuratedModelFile model, CuratedSlice slice)
    {
        var events = slice.Events.Count > 0
            ? slice.Events
            : eventsOf(model, x => slice.Aggregates.Count > 0 && x.Aggregates.Intersect(slice.Aggregates).Any())
              ?? eventsOf(model, x => slice.Domain is not null && x.Domain == slice.Domain)
              ?? eventsOf(model, _ => true)
              ?? [];

        return events.Select(name => new ViewSource(name, identityFieldFor(model, name))).ToList();
    }

    private static List<string>? eventsOf(CuratedModelFile model, Func<CuratedSlice, bool> predicate)
    {
        var events = model.Slices.Where(predicate)
            .SelectMany(x => x.Events)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return events.Count > 0 ? events : null;
    }

    /// <summary>
    /// A Guid field of the event's record, preferring one whose name ends in <c>Id</c> — the
    /// routing key a <c>MultiStreamProjection</c> slices by. Read from the slice that DECLARES
    /// the event, because that is the slice whose scaffold emits the record: routing on a field
    /// some other slice's scenario mentioned would not compile.
    /// </summary>
    private static string? identityFieldFor(CuratedModelFile model, string eventName)
    {
        var declaring = model.Slices.FirstOrDefault(x => x.Events.Contains(eventName));
        if (declaring is null) return null;

        var guids = fieldsFor(declaring, eventName).Where(x => x.Type == "Guid").ToList();
        return (guids.FirstOrDefault(x => x.Name.EndsWith("Id", StringComparison.Ordinal)) is { Name: { } named }
            ? named
            : guids.Select(x => x.Name).FirstOrDefault());
    }

    /// <summary>
    /// Whether this slice's refusals are about the aggregate's <b>state</b> rather than the
    /// request's shape — which the model already says, with no new field: it is whether the
    /// refusing scenario arranged any history (issue #238).
    /// </summary>
    /// <remarks>
    /// The refusals a scaffold writes as TODOs are copied from the model's <c>validationFails:</c>,
    /// and in a real chapter every one of them reads like <em>this appointment was cancelled</em>
    /// or <em>this appointment is already completed</em>. A guard given the request alone cannot
    /// answer either question, so the scaffolded signature made the scaffolded TODO impossible to
    /// fill — and the point of a scaffold is that filling it in is a decision, not a redesign. An
    /// agent that has to change the guard's signature has to re-derive Wolverine's compound-handler
    /// rules to know it may, which is exactly the token cost this engine exists to remove.
    ///
    /// Any refusing scenario with history widens the signature: the state-bearing parameter is a
    /// superset, so a slice refusing on both grounds is still one guard.
    /// </remarks>
    public static bool RefusesOnState(CuratedSlice slice)
        => slice.Specifications?.Scenarios.Any(x =>
               x.Given.Count > 0 && x.Then.Any(t => t.ValidationFails is not null)) ?? false;

    /// <summary>
    /// Whether this slice's file is the one that declares an event's record. Two slices may
    /// legally name the same event — the importer folds by command, not by event — and emitting
    /// the record into both files is two declarations of one type in one namespace, which does
    /// not compile. First declaring slice in model order owns it, the same rule that gives an
    /// aggregate one file (#222) and a trigger contract one file (#223).
    /// </summary>
    private static bool declaresEventRecord(CuratedModelFile model, CuratedSlice slice, string eventName)
        => ReferenceEquals(model.Slices.First(x => x.Events.Contains(eventName)), slice);

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
                files[$"{domain}/{slice.Name}.cs"] =
                    withHeader(ScaffoldFrame.Render(new ViewSliceFrame(slice, ViewSourcesFor(model, slice),
                        fieldsFor(slice, ReadModelFor(slice)))));
                break;
        }

        return files;
    }

    /// <summary>
    /// The whole decision about one slice, derived once. Every emitter reads it — the slice's own
    /// file, <see cref="ScaffoldAggregates"/> (which must know whether a <c>[WriteModel]</c> is
    /// bound at all before emitting a type for one), <see cref="ScaffoldTriggerContracts"/>, and
    /// the feature writer, whose act step has to name the type the code actually accepts.
    /// </summary>
    public static SlicePlan PlanFor(CuratedModelFile model, CuratedSlice slice)
    {
        var visibility = BusVisibility.Resolve(model, slice);

        // The collapsed default (CritterStackSamples#13): an HTTP-triggered command slice IS its
        // endpoint. The two-hop translation + message-handler shape is opt-in for bus-visible
        // commands only — and the model itself opts in (issue #218): a `messages:` entry another
        // slice handles off the bus selects the cascading shape, no flag.
        //
        // The pure translation front (#218): every consequence of this slice is a bus-visible
        // command and it appends nothing itself, so the endpoint is exactly the opt-in two-hop
        // shape — selected by the model rather than by hand.
        var shape = slice.Pattern != "Command" || slice.Trigger?.Kind is not ("Http" or "Human")
            ? SliceShape.WriteModelHandler
            : slice.Events.Count == 0 && visibility.Cascaded.Count == 1
                ? SliceShape.Translation
                : SliceShape.CollapsedEndpoint;

        return new SlicePlan(
            slice,
            shape,
            Command: slice.Command ?? slice.Name,
            Aggregate: AggregateFor(slice),
            ArrangeAggregate: ArrangeAggregateFor(model, slice),
            StartsStream: shape != SliceShape.Translation && CreatesTheStream(model, slice),
            AggregateWarnings: arrangeWarnings(model, slice),
            Route: $"/api/{(slice.Domain ?? "app").ToLowerInvariant()}/{slice.Name.ToLowerInvariant()}",
            Trigger: TriggerOrigins.Resolve(model, slice),
            Visibility: visibility);
    }

    /// <summary>
    /// What the feature says out loud when its arrange steps cannot be trusted: events belonging
    /// to more than one aggregate, or to none this model declares.
    /// </summary>
    private static IReadOnlyList<string> arrangeWarnings(CuratedModelFile model, CuratedSlice slice)
    {
        if (slice.Aggregates.Count > 0) return [];

        var resolved = arrangedAggregates(model, slice);
        if (resolved.Count > 1)
        {
            return
            [
                $"WARNING: the events this slice arranges belong to several aggregates ({string.Join(", ", resolved)});",
                $"the steps below use {resolved[0]}. Declare `aggregates:` on the slice to say which stream it means."
            ];
        }

        if (resolved.Count == 0 && slice.Pattern == "View"
            && (slice.Specifications?.Scenarios ?? []).Any(x => x.Given.Count > 0))
        {
            return
            [
                "WARNING: no slice in this model declares an aggregate for the events arranged below, so the",
                "arrange steps are omitted. Declare `aggregates:` on the slice whose events these are."
            ];
        }

        return [];
    }

    private static string commandSlice(CuratedModelFile model, CuratedSlice slice)
    {
        var frames = new List<ScaffoldFrame>();
        var plan = PlanFor(model, slice);
        var command = plan.Command;

        var collapsed = plan.OverHttp;
        var translation = plan.Shape == SliceShape.Translation;

        var visibility = plan.Visibility;

        // Event records, fields synthesized from element hints + scenario columns. Only the
        // first slice to declare an event emits its record — see declaresEventRecord.
        foreach (var @event in slice.Events.Where(x => declaresEventRecord(model, slice, x)))
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

        var route = plan.Route;
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
                startsStream: plan.StartsStream,
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
                     && PlanFor(model, slice).Shape != SliceShape.Translation)
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
            .Select(slice => (Slice: slice, Origin: PlanFor(model, slice).Trigger))
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
                group => feature(group.Key, group.Select(slice => PlanFor(model, slice)).ToList()));
    }

    private static string feature(string featureName, IReadOnlyList<SlicePlan> plans)
    {
        var writer = new SourceWriter();
        var first = plans[0].Slice;

        if (first.Domain is not null) writer.WriteLine($"@domain:{first.Domain}");
        writer.WriteLine($"Feature: {featureName}");
        if (first.Trigger?.Label is { } featureLabel) writer.WriteLine($"  Triggered by {featureLabel}");

        // Which fixture binds these steps is decided by the same plan that decided the code's
        // shape, so say it here rather than leaving it to be discovered as an unbound step. A
        // feature whose acts POST needs HttpGrammars, which only CritterStackHttpFixture carries.
        writer.BlankLine();
        if (plans.Any(x => x.OverHttp))
        {
            writer.WriteLine("  # Fixture: derive from CritterStackHttpFixture. At least one act below POSTs to a");
            writer.WriteLine("  # collapsed endpoint, and `is posted to` is HttpGrammars' step — CritterStackFixture");
            writer.WriteLine("  # alone carries the store vocabulary but not the HTTP one. Routes here are absolute,");
            writer.WriteLine("  # so leave the module's route prefix empty.");
        }
        else
        {
            writer.WriteLine("  # Fixture: derive from CritterStackFixture — every act below dispatches over the bus.");
        }

        foreach (var plan in plans)
        {
            writeScenarios(writer, plan);
        }

        return writer.Code();
    }

    private static void writeScenarios(ISourceWriter writer, SlicePlan plan)
    {
        var slice = plan.Slice;
        var specs = slice.Specifications!;

        // A literal "TODO" here names a type the generator cannot resolve (BOBCAT011), which
        // fails the spec project — the .feature half of the same defect as issue #226. Null is
        // the View slice with no write model: there is no type to name, and inventing one is
        // that same build error (issue #240).
        var aggregate = plan.ArrangeAggregate;

        foreach (var scenario in specs.Scenarios)
        {
            var streamId = streamIdFor(scenario.Name);

            writer.BlankLine();
            writer.WriteLine($"  @slice:{slice.Name}");
            writer.WriteLine($"  Scenario: {scenario.Name}");

            foreach (var warning in plan.AggregateWarnings)
            {
                writer.WriteLine($"    # {warning}");
            }

            if (aggregate is not null)
            {
                writer.WriteLine($"    Given no events for {aggregate} \"{streamId}\"");

                foreach (var given in scenario.Given)
                {
                    writer.WriteLine($"    And events for {aggregate}");
                    table(writer, "      ", new[] { "Event" }.Concat(given.With.Keys),
                        new[] { given.Event }.Concat(expand(given.With.Values, streamId)));
                }
            }

            if (scenario.When is not null)
            {
                foreach (var warning in strandedActWarnings(plan, scenario))
                {
                    writer.WriteLine($"    # {warning}");
                }

                // The act names the type the emitted code ACCEPTS, from the plan — not the
                // command the model happened to write in the scenario. Those differ exactly where
                // issue #231 bit: a collapsed endpoint has no bus-visible command type at all,
                // and an automation's handler takes its trigger event, not the slice's command.
                writer.WriteLine($"    {plan.ActStep}");
                if (scenario.When.With.Count > 0)
                {
                    table(writer, "      ", scenario.When.With.Keys, expand(scenario.When.With.Values, streamId));
                }
            }

            foreach (var then in scenario.Then)
            {
                if (then.Event is not null)
                {
                    writer.WriteLine($"    Then {then.Event} is emitted");
                    if (then.With.Count > 0) table(writer, "      ", then.With.Keys, expand(then.With.Values, streamId));
                }
                else if (then.ReadModel is not null)
                {
                    // A document keyed by anything but its stream is the identity-bearing step
                    // (issue #236) — a fan-out read model is keyed by an owner or a tenant, and
                    // the single-stream shortcut would load the scenario's stream instead.
                    writer.WriteLine(then.Id is null
                        ? $"    Then the {then.ReadModel} read model contains"
                        : $"    Then the {then.ReadModel} read model with id \"{expand(then.Id, streamId)}\" contains");
                    if (then.Contains.Count > 0) table(writer, "      ", then.Contains.Keys, expand(then.Contains.Values, streamId));
                }
                else if (then.ValidationFails is not null)
                {
                    foreach (var step in plan.RefusalSteps(then.ValidationFails))
                    {
                        writer.WriteLine($"    {step}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// The one token a curated scenario may write in a <c>with:</c> or <c>contains:</c> value:
    /// <c>{streamId}</c> stands for the stream this scenario runs against, and the feature writer
    /// expands it to the same id the scenario's <c>Given no events for …</c> step establishes.
    /// </summary>
    /// <remarks>
    /// Issue #235. A collapsed endpoint computes its stream from the request body — <c>[Identity]
    /// public Guid AppointmentId</c> — and <c>HttpGrammars.WhenCommandIsPosted</c> builds that body
    /// from the act's table and nothing else. So without a way to say "this scenario's stream", the
    /// act writes to whatever stream the body happens to name, which is never the stream the
    /// <c>Given</c> events reached. The happy paths still pass (the endpoint starts a fresh stream
    /// and <c>Then X is emitted</c> reads the tracked session, not the store) and — far worse — so
    /// do the refusals, because the guard sees an empty aggregate and refuses nothing. A spec that
    /// is green when the behaviour is absent is worse than a red one.
    ///
    /// The id cannot come from the model, because it is generated downstream of it. Resolving the
    /// token entirely inside <see cref="writeScenarios"/> keeps the emitted <c>.feature</c> literal
    /// and readable, and needs no grammar change at all.
    /// </remarks>
    public const string StreamIdToken = "{streamId}";

    private static bool isStreamIdToken(string value)
        => value.Trim().Equals(StreamIdToken, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> expand(IEnumerable<string> values, string streamId)
        => values.Select(x => expand(x, streamId));

    private static string expand(string value, string streamId)
        => value.Replace(StreamIdToken, streamId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The scenario that arranges history and then acts on a stream it never named. Report, never
    /// act: the fix is a <c>{streamId}</c> in the model's <c>with:</c>, and the scaffolder cannot
    /// know which field of the request carries the identity — so it says so in the feature, where
    /// whoever fills the slice in will read it.
    /// </summary>
    private static IEnumerable<string> strandedActWarnings(SlicePlan plan, CuratedScenario scenario)
    {
        if (!plan.OverHttp || scenario.Given.Count == 0) yield break;
        if (scenario.When!.With.Values.Any(isStreamIdToken)) yield break;

        yield return "WARNING: this scenario arranges history, but the act names no stream — the endpoint";
        yield return "computes its identity from the body below, so it will address a DIFFERENT stream than";
        yield return $"the Given above. Give the model's `with:` the identity field with the value \"{StreamIdToken}\".";
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

            // A read model's columns are the ones its scenarios assert on (issue #240) — the
            // other half of the model that was sitting there unused while the scaffolded class
            // came out holding an Id and a comment.
            foreach (var then in scenario.Then.Where(x => x.ReadModel == typeName))
            foreach (var (name, value) in then.Contains)
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
        // The scenario's own stream id, expanded by the feature writer (issue #235). It is always
        // a Guid, and it must never fall through to the sample-value inference below — a literal
        // "{streamId}" parses as nothing and would type the identity field as a string.
        if (isStreamIdToken(sketch)) return "Guid";
        if (KnownTypes.Contains(sketch)) return sketch is "guid" or "Guid" ? "Guid" : sketch;
        if (Guid.TryParse(sketch, out _)) return "Guid";
        if (bool.TryParse(sketch, out _)) return "bool";
        if (int.TryParse(sketch, out _)) return "int";
        if (decimal.TryParse(sketch, out _)) return "decimal";
        if (DateTimeOffset.TryParse(sketch, out _)) return "DateTimeOffset";
        return "string";
    }
}
