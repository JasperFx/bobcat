using System.Text;

namespace Bobcat.EventModel.Emlang;

public sealed record EmlangImportResult(ImportedEventModel Model, IReadOnlyList<string> Report)
{
    /// <summary>
    /// bobcat#454: every guess the import made and every gap it found, each with a stable code, its
    /// evidence and the flag that settles it. The same sentences are in <see cref="Report"/>.
    /// </summary>
    public IReadOnlyList<ImportCallout> Callouts { get; init; } = [];

    /// <summary>The board as imported: only the chapters asked for, which is the board the spec writer reads.</summary>
    public EmlangBoard? Board { get; init; }
}

/// <summary>
/// Segments an emlang board into curated slices (issue #202). An emlang chapter is a persona
/// timeline holding many slices, and the export's only structure is step order — so this is a
/// pure, deliberately conservative interpretation that reports every guess it makes. The output
/// is a curated file a human corrects, not a descriptor: emlang → curated → descriptor, one
/// loader, two front doors.
/// </summary>
/// <remarks>
/// The rules, in order of application to a chapter's step run:
/// <list type="bullet">
/// <item>A <c>c:</c> starts a slice named for the command; the <c>e:</c> steps that follow it
/// (until the next screen, command, or view) are its emitted events.</item>
/// <item>A command with a <c>triggeredBy</c> prop — or a <c>System/</c> actor and no preceding
/// screen — is an <c>Automation</c> slice (kind <c>MessageHandler</c>); anything else is a
/// <c>Command</c> slice triggered by the last unconsumed screen (<c>Human</c>).</item>
/// <item>A <c>v:</c> becomes a <c>View</c> slice for that read model; its sample-data props are
/// kept as element hints.</item>
/// <item>An <c>x:</c> becomes a prose hotspot on the open slice.</item>
/// <item>A chapter's GWT tests attach to the slice whose command the <c>when</c> names (or the
/// view slice its <c>then</c> asserts), becoming curated scenarios.</item>
/// <item>The same command or read model appearing in several chapters folds into one slice —
/// slice name is the merge key everywhere.</item>
/// </list>
/// </remarks>
public static class EmlangImport
{
    private static readonly string[] SpecialProps = ["triggeredBy", "module", "cascadedTo"];

    public static EmlangImportResult ToCurated(EmlangBoard board, string modelName, string? @namespace = null)
        => ToCurated(board, modelName, @namespace, null);

    /// <inheritdoc cref="ToCurated(EmlangBoard, string, string?)"/>
    /// <param name="aggregates">
    /// <c>--aggregate Slice=Type</c> overrides (bobcat#444), which win over the aggregates inferred from
    /// the examples. Empty or null infers every one, and calls each out.
    /// </param>
    public static EmlangImportResult ToCurated(EmlangBoard board, string modelName, string? @namespace,
        IReadOnlyList<AggregateOverride>? aggregates)
        => ToCurated(board, modelName, @namespace, aggregates, null);

    /// <inheritdoc cref="ToCurated(EmlangBoard, string, string?, IReadOnlyList{AggregateOverride}?)"/>
    /// <param name="chapters">
    /// Import only these chapters (bobcat#454); every chapter when null or empty. The rest of the board
    /// is still read, as evidence: an example whose only earlier event comes from a chapter left out
    /// is called out as possibly starting the stream (<see cref="ImportCalloutCodes.StreamStartFromUnimportedChapter"/>).
    /// </param>
    /// <exception cref="ArgumentException">A chapter the board does not have.</exception>
    public static EmlangImportResult ToCurated(EmlangBoard board, string modelName, string? @namespace,
        IReadOnlyList<AggregateOverride>? aggregates, IReadOnlyCollection<string>? chapters)
    {
        var whole = board;
        board = SelectChapters(board, chapters);
        var unimported = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chapter in whole.Chapters.Where(x => !board.Chapters.Contains(x)))
        {
            foreach (var step in chapter.Steps.Where(x => x.Kind == EmlangElementKind.Event))
            {
                unimported.TryAdd(AggregateInference.Name(step.Label), chapter.ChapterTitle);
            }
        }

        var model = new ImportedEventModel { Schema = 1, Model = modelName, Namespace = @namespace };
        var report = new List<string>();
        var byName = new Dictionary<string, CuratedSlice>(StringComparer.Ordinal);

        var shape = new List<ImportCallout>();
        var touched = new Dictionary<EmlangChapter, List<CuratedSlice>>();
        foreach (var chapter in board.Chapters)
        {
            touched[chapter] = segmentChapter(chapter, model, byName, report, shape);
        }

        foreach (var chapter in board.Chapters)
        {
            attachTests(chapter, touched[chapter], byName, report);
        }

        report.Add($"{model.Slices.Count} slice(s) from {board.Chapters.Count} chapter(s).");

        // bobcat#444: every command gets an aggregate, declared or inferred, and every inference is said
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in board.Chapters.SelectMany(x => x.Steps)
                     .Where(x => x.Kind == EmlangElementKind.Event && x.Stream is { Length: > 0 }))
        {
            declared.TryAdd(PascalName(step.Label), step.Stream!);
        }

        var callouts = AggregateInference.Apply(model, declared, aggregates, report, unimported);

        // After the aggregates, which reset each slice's callouts before writing their own
        shape.AddRange(boardShape(model, report));
        return new EmlangImportResult(model, report) { Callouts = [.. callouts, .. shape], Board = board };
    }

    /// <summary>
    /// The board cut to the named chapters (bobcat#454), in the board's order; the whole board when
    /// none are named.
    /// </summary>
    /// <exception cref="ArgumentException">A chapter the board does not have, with the ones it does.</exception>
    public static EmlangBoard SelectChapters(EmlangBoard board, IReadOnlyCollection<string>? chapters)
    {
        if (chapters is null || chapters.Count == 0) return board;

        // A name selects the board's chapter of that name (every slice in it) or, as before, the
        // one slice of that name (bobcat#845)
        var unknown = chapters.Where(x => board.Chapters.All(c => c.Name != x && c.ChapterTitle != x)).ToList();
        if (unknown.Count > 0)
        {
            var known = board.Chapters.Select(x => x.ChapterTitle).Distinct(StringComparer.Ordinal);
            throw new ArgumentException(
                $"No chapter named {string.Join(", ", unknown.Select(x => $"'{x}'"))}. The board has: {string.Join(", ", known)}",
                nameof(chapters));
        }

        return new EmlangBoard(board.Chapters.Where(x => chapters.Contains(x.Name) || chapters.Contains(x.ChapterTitle)).ToList());
    }

    /// <returns>Every slice the chapter opened or folded into, in order.</returns>
    /// <param name="shape">Where the board's own gaps are called out (bobcat#847): a screen no command follows, an exception no command precedes.</param>
    private static List<CuratedSlice> segmentChapter(EmlangChapter chapter, ImportedEventModel model,
        Dictionary<string, CuratedSlice> byName, List<string> report, List<ImportCallout> shape)
    {
        string? pendingScreen = null;
        CuratedSlice? current = null;
        var touched = new List<CuratedSlice>();

        // Issue #422: events before any command are not orphans until the chapter says so. The
        // next command is an automation triggered by the last of them (a processor slice), or the
        // next view folds them; only what neither takes is reported, at the chapter's end.
        var unattached = new List<EmlangStep>();

        // Issue #297: the events a view folds are the `e:` steps since the chapter start or the
        // last `v:`. The segmentation already used that run to decide a `v:` opens a View slice;
        // now the run is recorded on the slice as ConsumedEvents rather than discarded.
        var pendingViewInputs = new List<string>();

        foreach (var step in chapter.Steps)
        {
            switch (step.Kind)
            {
                case EmlangElementKind.Screen:
                    if (pendingScreen is not null) triggerWithoutCommand(chapter, pendingScreen, report, shape);
                    pendingScreen = step.Label;
                    current = null;
                    break;

                case EmlangElementKind.Command:
                    current = commandSlice(chapter, step, pendingScreen, unattached.LastOrDefault()?.Label, model, byName, report);
                    if (!touched.Contains(current)) touched.Add(current);
                    pendingScreen = null;
                    unattached.Clear();
                    break;

                case EmlangElementKind.Event:
                    var eventName = PascalName(step.Label);
                    if (!pendingViewInputs.Contains(eventName)) pendingViewInputs.Add(eventName);

                    if (current is null)
                    {
                        unattached.Add(step);
                        break;
                    }

                    addEvent(current, step);
                    break;

                case EmlangElementKind.View:
                    var view = viewSlice(chapter, step, pendingViewInputs, model, byName, report);
                    if (!touched.Contains(view)) touched.Add(view);
                    pendingViewInputs = new List<string>();
                    unattached.Clear();
                    current = null;
                    break;

                case EmlangElementKind.Error:
                    if (current is null)
                    {
                        var orphan = $"⚠ chapter '{chapter.Name}': exception '{step.Label}' has no command before it — not attached to a slice.";
                        report.Add(orphan);
                        shape.Add(new ImportCallout("", ImportCalloutCodes.OrphanException, [], orphan, null) { Chapters = [chapter.Name] });
                        break;
                    }

                    current.Hotspots.Add(step.Label);
                    break;
            }
        }

        foreach (var step in unattached)
        {
            report.Add($"⚠ chapter '{chapter.Name}': event '{step.Label}' precedes any command — not attached to a slice.");
        }

        if (pendingScreen is not null) triggerWithoutCommand(chapter, pendingScreen, report, shape);

        return touched;
    }

    private static void triggerWithoutCommand(EmlangChapter chapter, string screen, List<string> report, List<ImportCallout> shape)
    {
        var message = $"⚠ chapter '{chapter.Name}': screen '{screen}' is followed by no command — it triggers nothing.";
        report.Add(message);
        shape.Add(new ImportCallout("", ImportCalloutCodes.TriggerWithoutCommand, [], message, null) { Chapters = [chapter.Name] });
    }

    /// <summary>
    /// bobcat#847: the Emlang Project's two slice-level lint rules, judged over the whole board once
    /// every chapter has folded in — a command folded from two chapters emits in either, and a view is
    /// fed by an event in any slice, which is also how <c>emlang lint</c> reads it.
    /// </summary>
    private static IEnumerable<ImportCallout> boardShape(ImportedEventModel model, List<string> report)
    {
        foreach (var slice in model.Slices)
        {
            if (slice.Pattern is "Command" or "Automation" or "Translation" && slice.Events.Count == 0 && slice.Hotspots.Count == 0)
            {
                var message = $"⚠ slice '{slice.Name}': the command emits no event and raises no exception — nothing on the board says what it does.";
                report.Add(message);
                slice.Callouts.Add(message);
                yield return new ImportCallout(slice.Name, ImportCalloutCodes.CommandWithoutEvent, [], message, null);
            }

            if (slice.Pattern is "View" && slice.ConsumedEvents.Count == 0)
            {
                var message = $"⚠ slice '{slice.Name}': the view consumes no event — no event precedes '{slice.Name}' in any chapter.";
                report.Add(message);
                slice.Callouts.Add(message);
                yield return new ImportCallout(slice.Name, ImportCalloutCodes.ViewWithoutEvent, [], message, null);
            }
        }
    }

    private static CuratedSlice commandSlice(EmlangChapter chapter, EmlangStep step, string? pendingScreen,
        string? triggeringEvent, ImportedEventModel model, Dictionary<string, CuratedSlice> byName, List<string> report)
    {
        var name = PascalName(step.Label);
        if (byName.TryGetValue(name, out var existing))
        {
            report.Add($"chapter '{chapter.Name}': command '{step.Label}' folded into existing slice '{name}'."
                       + keptChapter(existing, chapter));
            return existing;
        }

        // A command after an event and no screen is a processor slice: the event triggers it
        var triggeredBy = step.Props.GetValueOrDefault("triggeredBy")
                          ?? (pendingScreen is null ? triggeringEvent : null);
        var inferredAutomation = triggeredBy is not null
                                 || (step.Actor.Equals("System", StringComparison.OrdinalIgnoreCase) && pendingScreen is null);

        // bobcat#845: a pattern the board declares (an EM Spec sliceType) is honoured over the
        // inference above, which stays as the rule for emlang, whose slices carry no type. A View
        // sliceType cannot describe a slice that opens with a command, so it is left to the inference
        var declared = chapter.SliceType is "Command" or "Automation" or "Translation" ? chapter.SliceType : null;
        var pattern = declared ?? (inferredAutomation ? "Automation" : "Command");
        var isAutomation = pattern is "Automation" or "Translation";

        var slice = new CuratedSlice
        {
            Name = name,
            Command = name,
            Pattern = pattern,
            Domain = step.Props.GetValueOrDefault("module"),
            Chapter = chapter.ChapterTitle,
            Trigger = isAutomation
                ? new CuratedTrigger { Kind = "MessageHandler", Label = triggeredBy }
                : pendingScreen is null
                    ? null
                    : new CuratedTrigger { Kind = "Human", Label = pendingScreen },
            Notes = note($"From chapter '{chapter.Name}', actor '{step.Actor}'.", step, chapter),
            Description = chapter.Description,
        };

        hints(slice, name, sketches(step.Props, step.Values), step.Description, step.Identities, step.Fields);
        model.Slices.Add(slice);
        byName[name] = slice;
        report.Add($"chapter '{chapter.Name}': {slice.Pattern} slice '{name}'"
                   + (slice.Trigger?.Label is { } label ? $" triggered by '{label}'" : "")
                   + (declared is not null ? " (pattern from the board's sliceType)." : "."));
        return slice;
    }

    private static void addEvent(CuratedSlice slice, EmlangStep step)
    {
        var name = PascalName(step.Label);
        if (!slice.Events.Contains(name)) slice.Events.Add(name);

        hints(slice, name, sketches(step.Props, step.Values), step.Description, step.Identities, step.Fields);
    }

    private static CuratedSlice viewSlice(EmlangChapter chapter, EmlangStep step, List<string> consumed,
        ImportedEventModel model, Dictionary<string, CuratedSlice> byName, List<string> report)
    {
        var readModel = PascalName(step.Label);
        if (byName.TryGetValue(readModel, out var existing))
        {
            if (!existing.ReadModels.Contains(readModel)) existing.ReadModels.Add(readModel);
            foreach (var name in consumed.Where(x => !existing.ConsumedEvents.Contains(x)))
            {
                existing.ConsumedEvents.Add(name);
            }

            hints(existing, readModel, sketches(step.Props, step.Values), step.Description, step.Identities, step.Fields);
            report.Add($"chapter '{chapter.Name}': view '{step.Label}' folded into existing slice '{readModel}'."
                       + keptChapter(existing, chapter));
            reportConsumed(chapter, existing, report);
            return existing;
        }

        var slice = new CuratedSlice
        {
            Name = readModel,
            Pattern = "View",
            Domain = step.Props.GetValueOrDefault("module"),
            Chapter = chapter.ChapterTitle,
            ReadModels = [readModel],
            ConsumedEvents = [.. consumed],
            Notes = note($"From chapter '{chapter.Name}', actor '{step.Actor}'.", step, chapter),
            Description = chapter.Description,
        };

        hints(slice, readModel, sketches(step.Props, step.Values), step.Description, step.Identities, step.Fields);
        model.Slices.Add(slice);
        byName[readModel] = slice;
        report.Add($"chapter '{chapter.Name}': View slice '{readModel}'.");
        reportConsumed(chapter, slice, report);
        return slice;
    }

    /// <summary>
    /// Issue #298: a slice folded from a second chapter keeps the FIRST chapter — the descriptor
    /// carries one chapter per slice, and the first chapter to name the slice is where the board
    /// introduces it. Said in the report rather than done silently, because a reader of the board
    /// sees the slice in both chapters and the canvas will show it under one band.
    /// </summary>
    private static string keptChapter(CuratedSlice existing, EmlangChapter chapter)
        => existing.Chapter is { } kept && !string.Equals(kept, chapter.ChapterTitle, StringComparison.Ordinal)
            ? $" Kept chapter '{kept}'; the descriptor carries one chapter per slice."
            : string.Empty;

    private static void reportConsumed(EmlangChapter chapter, CuratedSlice slice, List<string> report)
    {
        if (slice.ConsumedEvents.Count == 0)
        {
            report.Add($"⚠ chapter '{chapter.Name}': View slice '{slice.Name}' consumes no event — no `e:` precedes it since the chapter start or the last `v:`.");
            return;
        }

        report.Add($"chapter '{chapter.Name}': View slice '{slice.Name}' consumes {slice.ConsumedEvents.Count} event(s): {string.Join(", ", slice.ConsumedEvents)}.");
    }

    /// <summary>
    /// Non-special props are field/sample hints for the scaffolding layer, never roles. The first
    /// sketch of a field wins, and steps are read before tests, so a type a step declares
    /// (<c>email: string</c>) is never displaced by a test's sample value.
    /// </summary>
    private static void hints(CuratedSlice slice, string typeName, IReadOnlyDictionary<string, string> props, string? description,
        IReadOnlyList<string>? identities = null, IReadOnlyList<EmlangField>? declared = null)
    {
        var fields = props.Where(x => !SpecialProps.Contains(x.Key)).ToList();
        if (fields.Count == 0 && description is null && (identities is null || identities.Count == 0)) return;

        if (!slice.Elements.TryGetValue(typeName, out var element))
        {
            element = new CuratedElement();
            slice.Elements[typeName] = element;
        }

        element.Description ??= description;
        foreach (var (key, value) in fields)
        {
            element.Fields.TryAdd(key, value);
        }

        // bobcat#851: the typed declaration beside the sketch, first declaration wins like the sketch does
        foreach (var field in declared ?? [])
        {
            if (!SpecialProps.Contains(field.Name)) element.DeclaredFields.TryAdd(field.Name, Curated(field));
        }

        foreach (var identity in identities ?? [])
        {
            if (!element.Identities.Contains(identity)) element.Identities.Add(identity);
        }
    }

    /// <summary>
    /// A board field in the import's terms (bobcat#851): the EM Spec type word as the C# type the stub writer
    /// emits, and a <c>Custom</c> field with subfields as a nested record named by its <c>schema</c>, else by
    /// the field itself. A <c>Custom</c> field with no subfields is a <c>string</c>, as its sketch always was.
    /// </summary>
    public static CuratedField Curated(EmlangField field)
    {
        var custom = field.Type.Trim().Equals("Custom", StringComparison.OrdinalIgnoreCase);
        var type = custom
            ? field.Subfields.Count > 0 ? CSharpModelWriter.Identifiers.Sanitize(PascalName(field.Schema ?? field.Name)) : "string"
            : CuratedFieldTypes.TryInfer(boardTypeWord(field.Type), out var inferred) ? inferred : "string";

        return new CuratedField
        {
            Name = field.Name,
            Type = type.Length > 0 ? type : "string",
            Many = field.List,
            Optional = field.Optional,
            Identity = field.Identity,
            Generated = field.Generated,
            Pii = field.Pii,
            Tag = field.Tag,
            Mapping = field.Mapping,
            Description = field.Description,
            Subfields = custom ? field.Subfields.Select(Curated).ToList() : [],
        };
    }

    /// <summary>The EM Spec type enum in the spellings <see cref="CuratedFieldTypes"/> knows.</summary>
    private static string boardTypeWord(string type) => type.Trim().ToLowerInvariant() switch
    {
        "uuid" => "uuid",
        "int" or "integer" => "int",
        "long" => "long",
        "double" => "double",
        "decimal" or "number" => "decimal",
        "boolean" or "bool" => "boolean",
        "date" => "date",
        "datetime" => "datetime",
        _ => "string",
    };

    private static string? note(string provenance, EmlangStep step)
    {
        var cascaded = step.Props.GetValueOrDefault("cascadedTo");
        return cascaded is null ? provenance : $"{provenance} Cascades to: {cascaded}.";
    }

    /// <summary>The slice's board description (bobcat#846) under the provenance line, one note per line.</summary>
    private static string? note(string provenance, EmlangStep step, EmlangChapter chapter)
    {
        var text = note(provenance, step);
        if (chapter.Description is not { Length: > 0 } description) return text;

        var lines = description.Replace("\r\n", "\n").Split('\n').Select(x => x.TrimEnd()).Where(x => x.Length > 0);
        return text + "\n" + string.Join("\n", lines);
    }

    private static void attachTests(EmlangChapter chapter, List<CuratedSlice> touched,
        Dictionary<string, CuratedSlice> byName, List<string> report)
    {
        foreach (var test in chapter.Tests)
        {
            var command = test.When.FirstOrDefault(x => x.Kind == EmlangElementKind.Command);
            var readModel = test.Then.FirstOrDefault(x => x.Kind == EmlangElementKind.View);

            var target = command is not null
                ? byName.GetValueOrDefault(PascalName(command.Label))
                : readModel is not null
                    ? byName.GetValueOrDefault(PascalName(readModel.Label))
                    : null;

            // Issue #422: a test that names neither a command nor a view — "no order yet, so no
            // summary" — belongs to the slice it is written under, when that is unambiguous
            if (target is null && command is null && readModel is null && touched.Count == 1)
            {
                target = touched[0];
                report.Add($"chapter '{chapter.Name}': test '{test.Name}' names no command or view — attached to the chapter's only slice '{target.Name}'.");
            }

            if (target is null)
            {
                report.Add($"⚠ chapter '{chapter.Name}': test '{test.Name}' names no known slice — not attached.");
                continue;
            }

            // The scenario is named for the title its generated specification reports, not the
            // board's spelling of it: a projected test's title IS its method name read back as a
            // sentence, so "AppointmentConfirmed" runs as "appointment confirmed". Naming it
            // anything else links the definition to an identity no run ever reports.
            var scenario = ProjectedSpecNaming.ExampleScenarioTitle(test.Name);

            target.Specifications ??= new CuratedSpecifications();
            if (target.Specifications.Scenarios.Any(x => x.Name == scenario))
            {
                // Two chapters exercising one folded slice can carry the same test; the identity
                // must stay unique to join run evidence, so keep the first and say so.
                report.Add($"chapter '{chapter.Name}': test '{test.Name}' already exists on slice '{target.Name}' — kept the first.");
                continue;
            }

            foreach (var view in test.Given.Where(x => x.Kind == EmlangElementKind.View))
            {
                report.Add($"⚠ chapter '{chapter.Name}': test '{test.Name}' gives the view '{view.Label}', which cannot be arranged directly — arrange the events that build it.");
            }

            foreach (var refusal in test.Then.Where(x => x.Kind == EmlangElementKind.Error && x.Props.Count > 0))
            {
                report.Add($"⚠ chapter '{chapter.Name}': test '{test.Name}' refuses with '{refusal.Label}' carrying props ({string.Join(", ", refusal.Props.Keys)}); only the refusal is kept.");
            }

            // A test's example values are samples of the fields its elements carry, so they are
            // field hints as well as scenario values: often the only place a board says a field exists
            foreach (var reference in test.Given.Concat(test.When).Concat(test.Then)
                         .Where(x => x.Kind is EmlangElementKind.Command or EmlangElementKind.Event or EmlangElementKind.View))
            {
                hints(target, PascalName(reference.Label), sketches(reference.Props, reference.Values), description: null);
            }

            target.Specifications.Scenarios.Add(new CuratedScenario
            {
                Name = scenario,
                Source = test,
                SourceChapter = chapter,
                // bobcat#846: the typed record, bound to the slice it landed on
                Model = test.Model with
                {
                    Slice = target.Name,
                    Chapter = chapter.ChapterTitle,
                    Pattern = target.Pattern,
                    Status = test.Model.Status == Scenarios.ScenarioStatus.Normal ? chapter.ScenarioStatus : test.Model.Status,
                    Actor = test.Model.Actor ?? (chapter.Actors.Count == 1 ? chapter.Actors[0].Name : null),
                },
                Given = test.Given
                    .Where(x => x.Kind == EmlangElementKind.Event)
                    .Select(x => new CuratedGiven { Event = PascalName(x.Label), With = values(x) })
                    .ToList(),
                When = command is null ? null : new CuratedWhen { Command = PascalName(command.Label), With = values(command) },
                Then = test.Then.Where(x => x.Kind != EmlangElementKind.Screen).Select(thenEntry).ToList(),
            });
        }
    }

    /// <summary>
    /// A prop's sketch: its text, except that a list of values sketches as <c>List&lt;string&gt;</c>,
    /// the one collection a board can show (issue #423).
    /// </summary>
    private static IReadOnlyDictionary<string, string> sketches(IReadOnlyDictionary<string, string> props,
        IReadOnlyDictionary<string, object?> values)
        => props.ToDictionary(x => x.Key,
            x => values.GetValueOrDefault(x.Key) is List<object> ? CuratedFieldTypes.StringList : x.Value,
            StringComparer.Ordinal);

    private static Dictionary<string, string> values(EmlangRef reference)
        => new(reference.Props, StringComparer.Ordinal);

    private static CuratedThen thenEntry(EmlangRef reference) => reference.Kind switch
    {
        EmlangElementKind.View => new CuratedThen { ReadModel = PascalName(reference.Label), Contains = values(reference) },
        EmlangElementKind.Error => new CuratedThen { ValidationFails = reference.Label },
        _ => new CuratedThen { Event = PascalName(reference.Label), With = values(reference) },
    };

    /// <summary>
    /// The board's naming rule: runs of letters/digits from the display label, first character
    /// of each run upper-cased, concatenated — "RSVP Blocked: Event Full" → "RSVPBlockedEventFull".
    /// </summary>
    public static string PascalName(string label)
    {
        var result = new StringBuilder(label.Length);
        var startOfRun = true;

        foreach (var character in label)
        {
            if (!char.IsLetterOrDigit(character))
            {
                startOfRun = true;
                continue;
            }

            result.Append(startOfRun ? char.ToUpperInvariant(character) : character);
            startOfRun = false;
        }

        return result.ToString();
    }
}
