using System.Text;

namespace Bobcat.EventModel.Emlang;

public sealed record EmlangImportResult(CuratedModelFile Model, IReadOnlyList<string> Report);

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
    {
        var model = new CuratedModelFile { Schema = 1, Model = modelName, Namespace = @namespace };
        var report = new List<string>();
        var byName = new Dictionary<string, CuratedSlice>(StringComparer.Ordinal);

        foreach (var chapter in board.Chapters)
        {
            segmentChapter(chapter, model, byName, report);
        }

        foreach (var chapter in board.Chapters)
        {
            attachTests(chapter, byName, report);
        }

        report.Add($"{model.Slices.Count} slice(s) from {board.Chapters.Count} chapter(s).");
        return new EmlangImportResult(model, report);
    }

    private static void segmentChapter(EmlangChapter chapter, CuratedModelFile model,
        Dictionary<string, CuratedSlice> byName, List<string> report)
    {
        string? pendingScreen = null;
        CuratedSlice? current = null;

        foreach (var step in chapter.Steps)
        {
            switch (step.Kind)
            {
                case EmlangElementKind.Screen:
                    pendingScreen = step.Label;
                    current = null;
                    break;

                case EmlangElementKind.Command:
                    current = commandSlice(chapter, step, pendingScreen, model, byName, report);
                    pendingScreen = null;
                    break;

                case EmlangElementKind.Event:
                    if (current is null)
                    {
                        report.Add($"⚠ chapter '{chapter.Name}': event '{step.Label}' precedes any command — not attached to a slice.");
                        break;
                    }

                    addEvent(current, step);
                    break;

                case EmlangElementKind.View:
                    viewSlice(chapter, step, model, byName, report);
                    current = null;
                    break;

                case EmlangElementKind.Error:
                    if (current is null)
                    {
                        report.Add($"⚠ chapter '{chapter.Name}': exception '{step.Label}' has no open slice — not attached.");
                        break;
                    }

                    current.Hotspots.Add(step.Label);
                    break;
            }
        }
    }

    private static CuratedSlice commandSlice(EmlangChapter chapter, EmlangStep step, string? pendingScreen,
        CuratedModelFile model, Dictionary<string, CuratedSlice> byName, List<string> report)
    {
        var name = PascalName(step.Label);
        if (byName.TryGetValue(name, out var existing))
        {
            report.Add($"chapter '{chapter.Name}': command '{step.Label}' folded into existing slice '{name}'.");
            return existing;
        }

        var triggeredBy = step.Props.GetValueOrDefault("triggeredBy");
        var isAutomation = triggeredBy is not null
                           || (step.Actor.Equals("System", StringComparison.OrdinalIgnoreCase) && pendingScreen is null);

        var slice = new CuratedSlice
        {
            Name = name,
            Command = name,
            Pattern = isAutomation ? "Automation" : "Command",
            Domain = step.Props.GetValueOrDefault("module"),
            Trigger = isAutomation
                ? new CuratedTrigger { Kind = "MessageHandler", Label = triggeredBy }
                : pendingScreen is null
                    ? null
                    : new CuratedTrigger { Kind = "Human", Label = pendingScreen },
            Notes = note($"From chapter '{chapter.Name}', actor '{step.Actor}'.", step),
        };

        model.Slices.Add(slice);
        byName[name] = slice;
        report.Add($"chapter '{chapter.Name}': {slice.Pattern} slice '{name}'"
                   + (slice.Trigger?.Label is { } label ? $" triggered by '{label}'." : "."));
        return slice;
    }

    private static void addEvent(CuratedSlice slice, EmlangStep step)
    {
        var name = PascalName(step.Label);
        if (!slice.Events.Contains(name)) slice.Events.Add(name);

        hints(slice, name, step, description: null);
    }

    private static void viewSlice(EmlangChapter chapter, EmlangStep step, CuratedModelFile model,
        Dictionary<string, CuratedSlice> byName, List<string> report)
    {
        var readModel = PascalName(step.Label);
        if (byName.TryGetValue(readModel, out var existing))
        {
            if (!existing.ReadModels.Contains(readModel)) existing.ReadModels.Add(readModel);
            hints(existing, readModel, step, description: null);
            return;
        }

        var slice = new CuratedSlice
        {
            Name = readModel,
            Pattern = "View",
            Domain = step.Props.GetValueOrDefault("module"),
            ReadModels = [readModel],
            Notes = note($"From chapter '{chapter.Name}', actor '{step.Actor}'.", step),
        };

        hints(slice, readModel, step, description: null);
        model.Slices.Add(slice);
        byName[readModel] = slice;
        report.Add($"chapter '{chapter.Name}': View slice '{readModel}'.");
    }

    /// <summary>Non-special props are field/sample hints for the scaffolding layer, never roles.</summary>
    private static void hints(CuratedSlice slice, string typeName, EmlangStep step, string? description)
    {
        var fields = step.Props.Where(x => !SpecialProps.Contains(x.Key)).ToList();
        if (fields.Count == 0 && description is null) return;

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
    }

    private static string? note(string provenance, EmlangStep step)
    {
        var cascaded = step.Props.GetValueOrDefault("cascadedTo");
        return cascaded is null ? provenance : $"{provenance} Cascades to: {cascaded}.";
    }

    private static void attachTests(EmlangChapter chapter, Dictionary<string, CuratedSlice> byName, List<string> report)
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

            if (target is null)
            {
                report.Add($"⚠ chapter '{chapter.Name}': test '{test.Name}' names no known slice — not attached.");
                continue;
            }

            target.Specifications ??= new CuratedSpecifications();
            if (target.Specifications.Scenarios.Any(x => x.Name == test.Name))
            {
                // Two chapters exercising one folded slice can carry the same test; the identity
                // must stay unique to join run evidence, so keep the first and say so.
                report.Add($"chapter '{chapter.Name}': test '{test.Name}' already exists on slice '{target.Name}' — kept the first.");
                continue;
            }

            target.Specifications.Scenarios.Add(new CuratedScenario
            {
                Name = test.Name,
                Given = test.Given
                    .Where(x => x.Kind == EmlangElementKind.Event)
                    .Select(x => new CuratedGiven { Event = PascalName(x.Label) })
                    .ToList(),
                When = command is null ? null : new CuratedWhen { Command = PascalName(command.Label) },
                Then = test.Then.Select(thenEntry).ToList(),
            });
        }
    }

    private static CuratedThen thenEntry(EmlangRef reference) => reference.Kind switch
    {
        EmlangElementKind.View => new CuratedThen { ReadModel = PascalName(reference.Label) },
        EmlangElementKind.Error => new CuratedThen { ValidationFails = reference.Label },
        _ => new CuratedThen { Event = PascalName(reference.Label) },
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
