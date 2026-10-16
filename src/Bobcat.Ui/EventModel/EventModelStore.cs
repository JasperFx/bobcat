using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Events.EventModeling;

namespace Bobcat.Ui.EventModel;

/// <summary>
/// The console's copy of the current Event Model descriptor (issue #108) — design-time truth,
/// persisted beside the run archives so it survives a restart. Deliberately not per-run state:
/// run evidence joins onto it by spec identity, so it lives on its own files rather than in any
/// <c>RunProjection</c>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>One model, several PRODUCERS.</b> This used to be a single document with latest-push-wins,
/// and that quietly made the model unjoinable. The two halves of a model are compiled into
/// different assemblies: Wolverine's <c>event-model</c> export runs against the HOST and carries
/// slices with no <c>Specifications</c>, while a spec assembly's generated
/// <c>IEventModelDefinitionSource</c> carries the specs and is not visible to the host. Latest-wins
/// meant whichever pushed last erased the other, so <c>Specifications</c> was empty for every
/// consumer and no run outcome could ever be attributed to a slice. See CritterWatch#1212.
/// </para>
/// <para>
/// So a push names its SOURCE and replaces only that source's contribution;
/// <see cref="Read" /> merges them with <see cref="EventModelDescriptor.Merge" />, which folds
/// slices by name — exactly the join this needs.
/// </para>
/// <para>
/// ⚠️ <b>Why per-source rather than merging on push.</b> Merging into one stored document can only
/// ever ADD. A producer that renames or removes a slice would leave the old one behind forever,
/// and the model would drift further from the code with every push. Replacing one source's
/// contribution is what makes a re-push idempotent.
/// </para>
/// </remarks>
public sealed class EventModelStore
{
    /// <summary>
    /// The wire shape both viewers agree on: camelCase members, PascalCase enum values —
    /// exactly what <c>@jasperfx/event-model-vue</c>'s hand-written TypeScript mirror types
    /// (its contract spec pins the enum members), and what JasperFx's own wire round-trip
    /// tests exercise. Reading is enum-case-insensitive, so a producer serializing camelCase
    /// enum values is normalized rather than rejected. A slice pattern this build does not name
    /// reads as underived rather than refusing the descriptor (<see cref="TolerantSlicePatternConverter"/>),
    /// so the next pattern JasperFx appends cannot blank the canvas before Stoat catches up.
    /// </summary>
    public static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        Converters = { new TolerantSlicePatternConverter(), new JsonStringEnumConverter(namingPolicy: null) }
    };

    /// <summary>The source name a bare <c>PUT /api/event-model</c> writes to.</summary>
    public const string DefaultSource = "default";

    private readonly string _dataPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, EventModelDescriptor> _bySource = new(StringComparer.OrdinalIgnoreCase);
    private string? _name;

    public EventModelStore(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        _dataPath = dataPath;

        // Every source on disk, plus the pre-#1212 single file read as the default source so an
        // existing console keeps serving what it served before the split.
        foreach (var file in Directory.EnumerateFiles(dataPath, "event-model*.json"))
        {
            var source = sourceOf(Path.GetFileNameWithoutExtension(file));
            try
            {
                if (JsonSerializer.Deserialize<EventModelDescriptor>(File.ReadAllText(file), Wire) is { } d)
                {
                    _bySource[source] = d;
                    // #169 — recover the name across a restart, so the first broadcast after one is
                    // not anonymous.
                    _name ??= d.Name;
                }
            }
            catch (JsonException)
            {
                // A corrupt file must not stop the console booting. Skip it; the others still serve.
            }
        }
    }

    /// <summary>"event-model" is the legacy single document; "event-model.specs" is source "specs".</summary>
    private static string sourceOf(string fileStem)
    {
        var dot = fileStem.IndexOf('.');
        return dot < 0 ? DefaultSource : fileStem[(dot + 1)..];
    }

    private string fileFor(string source)
        => Path.Combine(_dataPath,
            source.Equals(DefaultSource, StringComparison.OrdinalIgnoreCase)
                ? "event-model.json"
                : $"event-model.{source}.json");

    /// <summary>
    /// The stored descriptor's JSON, or null when none has been published — the MERGE of every
    /// source contributing to the current model.
    /// </summary>
    /// <remarks>
    /// ⚠️ Only sources whose descriptor carries the current <see cref="Name" /> are merged. Two
    /// producers pushing DIFFERENT model names are two models, not two halves of one, and folding
    /// them would invent a model nobody wrote. The others stay on disk, so a later push of their
    /// name brings them back rather than losing them.
    /// </remarks>
    public string? Read()
    {
        lock (_gate)
        {
            if (_name is null) return null;

            var parts = _bySource.Values
                .Where(d => string.Equals(d.Name, _name, StringComparison.Ordinal))
                .ToArray();

            if (parts.Length == 0) return null;

            var merged = parts.Length == 1 ? parts[0] : EventModelDescriptor.Merge(_name, parts);
            return JsonSerializer.Serialize(merged, Wire);
        }
    }

    /// <summary>
    /// The stored descriptor's name, or null when none has been published yet. Issue #169 — the
    /// change broadcast names the model it is about, so a console watching two producers can say
    /// which one moved instead of just "something changed".
    /// </summary>
    public string? Name
    {
        get { lock (_gate) return _name; }
    }

    /// <summary>
    /// The source a spec assembly publishes its half under: its name with anything a file name will
    /// not take as '-' — the publisher's <c>SpecEventModelPublisher.SourceNameFor</c>, mirrored here,
    /// as every wire rule is, because the console references nothing of the publisher's.
    /// </summary>
    public static string SpecSourceFor(string suite)
    {
        var source = new string(suite.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return source.Length == 0 ? "specs" : source;
    }

    /// <summary>
    /// Drop from <paramref name="suite"/>'s spec half every specification its runner no longer has
    /// (bobcat#652): a renamed or deleted spec stayed bound to its slice on the canvas until a run
    /// republished the half, and a resident runner relaunched on a rebuild runs nothing. A data row
    /// keeps its method's identity bound. Returns true when anything was dropped.
    /// </summary>
    /// <remarks>
    /// Only ever removes. A new specification's slice is a compile-time fact the runner does not
    /// carry, so it reaches the canvas with the next run that publishes the half, as before.
    /// </remarks>
    public bool PruneSpecifications(string suite, IReadOnlyCollection<string> current)
    {
        var source = SpecSourceFor(suite);
        bool keeps(string identity)
            => current.Contains(identity)
               || current.Any(x => x.StartsWith(identity + " (", StringComparison.Ordinal) || x.StartsWith(identity + " [", StringComparison.Ordinal));

        lock (_gate)
        {
            if (!_bySource.TryGetValue(source, out var descriptor)) return false;

            var changed = false;
            var slices = descriptor.Slices.Select(slice =>
            {
                var kept = slice.Specifications.Where(spec => keeps(spec.Identity)).ToArray();
                if (kept.Length == slice.Specifications.Count) return slice;

                changed = true;
                return slice with { Specifications = kept };
            }).ToArray();

            if (!changed) return false;

            var pruned = descriptor with { Slices = slices };
            File.WriteAllText(fileFor(source), JsonSerializer.Serialize(pruned, Wire));
            _bySource[source] = pruned;
            return true;
        }
    }

    /// <summary>
    /// Validate and store a descriptor. The document is round-tripped through the typed
    /// JasperFx <see cref="EventModelDescriptor"/> so an unparseable push is a 400 at the
    /// endpoint rather than a blank canvas later, and so the stored copy is normalized to the
    /// wire shape whatever casing the producer used. Returns the parse failure, or null on
    /// success.
    /// </summary>
    public string? TryStore(string json, string? source = null)
    {
        source = string.IsNullOrWhiteSpace(source) ? DefaultSource : source.Trim();

        // The source becomes a file name, so it may not escape the data directory.
        if (source.Any(c => c == '.' || c == '/' || c == '\\' || Path.GetInvalidFileNameChars().Contains(c)))
        {
            return $"'{source}' is not a usable source name: letters, digits, '-' and '_' only";
        }

        EventModelDescriptor? descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize<EventModelDescriptor>(json, Wire);
        }
        catch (JsonException e)
        {
            return e.Message;
        }

        if (descriptor is null) return "the body was empty";
        if (string.IsNullOrWhiteSpace(descriptor.Name)) return "the descriptor has no name";

        lock (_gate)
        {
            // Replaces THIS source only — see the type's remarks for why merging on push cannot
            // work. Normalized on the way to disk so the stored copy is the wire shape whatever
            // casing the producer used.
            File.WriteAllText(fileFor(source), JsonSerializer.Serialize(descriptor, Wire));
            _bySource[source] = descriptor;
            _name = descriptor.Name;
        }

        return null;
    }
}
