using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Events.EventModeling;

namespace Bobcat.Ui.EventModel;

/// <summary>
/// Where one push of an Event Model half came from: the <c>root</c> / <c>repository</c> /
/// <c>branch</c> query parameters a producer sends on <c>PUT /api/event-model</c> (bobcat#532).
/// Every field is optional, because every field is optional on the wire — an absent one is "the
/// producer could not say", never a guess.
/// </summary>
public sealed record EventModelPushOrigin(string? Root, string? Repository, string? Branch)
{
    /// <summary>Null when the push said nothing at all about where it came from.</summary>
    public static EventModelPushOrigin? From(string? root, string? repository, string? branch)
    {
        static string? clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        var origin = new EventModelPushOrigin(clean(root), clean(repository), clean(branch));
        return origin is { Root: null, Repository: null, Branch: null } ? null : origin;
    }
}

/// <summary>One source's contribution to a model, as <c>GET /api/event-models</c> lists it (bobcat#799).</summary>
/// <param name="Source">The source name the half was pushed under (<c>default</c> for a bare <c>PUT</c>).</param>
/// <param name="PushedAt">When it was last pushed.</param>
/// <param name="PushedAtIsFileTime">
/// True for a half stored before the console recorded push times: <see cref="PushedAt"/> is then the
/// file's last-write time, which is when it was last written and so the best answer there is.
/// </param>
public sealed record EventModelSourceSummary(
    string Source,
    DateTimeOffset PushedAt,
    bool PushedAtIsFileTime,
    string? Root,
    string? Repository,
    string? Branch);

/// <summary>One model the console holds, with every source contributing to it, newest push first.</summary>
public sealed record EventModelSummary(
    string Name,
    DateTimeOffset LastPushedAt,
    int Slices,
    IReadOnlyList<EventModelSourceSummary> Sources);

/// <summary>
/// The console's copy of the Event Model descriptors (issue #108) — design-time truth, persisted
/// beside the run archives so it survives a restart. Deliberately not per-run state: run evidence
/// joins onto it by spec identity, so it lives on its own files rather than in any
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
/// <see cref="Read(string?)" /> merges them with <see cref="EventModelDescriptor.Merge" />, which
/// folds slices by name — exactly the join this needs. The hotspots the merge records
/// (<c>SourceDisagreement</c>) therefore exist only on a read, and only when both halves are here.
/// </para>
/// <para>
/// ⚠️ <b>Why per-source rather than merging on push.</b> Merging into one stored document can only
/// ever ADD. A producer that renames or removes a slice would leave the old one behind forever,
/// and the model would drift further from the code with every push. Replacing one source's
/// contribution is what makes a re-push idempotent.
/// </para>
/// <para>
/// ⚠️ <b>Several MODELS, too (bobcat#799).</b> A contribution is keyed by model name AND source.
/// Keyed by source alone, a second application pushing a bare <c>PUT</c> replaced the first one's
/// <c>default</c> half, and a model nobody had pushed for a fortnight stayed "current" and hid the
/// one being worked on, with no way to reach the other. Every model now stays reachable by name
/// (<see cref="Read(string?)"/>, <see cref="Models"/>), each push records when it arrived and where
/// from, and the bare read keeps its old meaning: the last model pushed.
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

    /// <summary>The folder under the data path holding one folder per model (bobcat#799).</summary>
    public const string ModelsFolder = "event-models";

    /// <summary>Each model folder's record of when, and from where, each of its sources was pushed.</summary>
    public const string PushesFile = "pushes.json";

    /// <summary>What a pre-#799 file in the data path is renamed to once its content has moved.</summary>
    public const string MigratedSuffix = ".migrated";

    private static readonly JsonSerializerOptions pushesWire = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _dataPath;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    // model name (ordinal: two spellings are two models) -> source (case-insensitive, it is a file name)
    private readonly Dictionary<string, Dictionary<string, Contribution>> _models = new(StringComparer.Ordinal);
    private string? _name;

    private sealed record Contribution(
        string Source,
        EventModelDescriptor Descriptor,
        DateTimeOffset PushedAt,
        bool PushedAtIsFileTime,
        EventModelPushOrigin? Origin);

    private sealed record PushRecord(
        DateTimeOffset PushedAt,
        bool PushedAtIsFileTime,
        string? Root,
        string? Repository,
        string? Branch);

    private sealed record PushesDocument(string Name, Dictionary<string, PushRecord> Sources);

    public EventModelStore(string dataPath, TimeProvider? time = null)
    {
        Directory.CreateDirectory(dataPath);
        _dataPath = dataPath;
        _time = time ?? TimeProvider.System;

        loadModelFolders();
        migrateLegacyFiles();

        // #169 — recover the name across a restart, so the first broadcast after one is not
        // anonymous. The newest push, rather than whichever file the directory listing happened to
        // return first, which is what the bare read means while the console is up.
        _name = _models
            .Select(m => (Name: m.Key, Last: m.Value.Values.Max(c => c.PushedAt)))
            .OrderByDescending(m => m.Last)
            .Select(m => m.Name)
            .FirstOrDefault();
    }

    private void loadModelFolders()
    {
        var root = Path.Combine(_dataPath, ModelsFolder);
        if (!Directory.Exists(root)) return;

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var pushes = readPushes(folder);

            foreach (var file in Directory.EnumerateFiles(folder, "event-model*.json"))
            {
                var source = sourceOf(Path.GetFileNameWithoutExtension(file));
                if (readDescriptor(file) is not { } descriptor) continue;

                var push = pushes?.Sources.GetValueOrDefault(source)
                           ?? new PushRecord(File.GetLastWriteTimeUtc(file), true, null, null, null);

                remember(descriptor, new Contribution(source, descriptor, push.PushedAt, push.PushedAtIsFileTime,
                    EventModelPushOrigin.From(push.Root, push.Repository, push.Branch)));
            }
        }
    }

    /// <summary>
    /// Every pre-#799 <c>event-model*.json</c> in the data path, moved into its model's folder. There
    /// was no push time then, so the file's last-write time stands in for it, flagged as such; the
    /// original is renamed rather than deleted, so nothing that was on disk is lost by an upgrade.
    /// </summary>
    private void migrateLegacyFiles()
    {
        foreach (var file in Directory.EnumerateFiles(_dataPath, "event-model*.json"))
        {
            // A corrupt file must not stop the console booting. Leave it where it is; the others still serve.
            if (readDescriptor(file) is not { } descriptor) continue;

            var source = sourceOf(Path.GetFileNameWithoutExtension(file));
            var contribution = new Contribution(source, descriptor, File.GetLastWriteTimeUtc(file), true, null);

            if (find(descriptor.Name, source) is null)
            {
                remember(descriptor, contribution);
                persist(descriptor.Name, contribution);
            }

            try
            {
                File.Move(file, file + MigratedSuffix, overwrite: true);
            }
            catch (IOException)
            {
                // Moved already, or held open; the copy in the model folder is what serves from here on.
            }
        }
    }

    private static EventModelDescriptor? readDescriptor(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<EventModelDescriptor>(File.ReadAllText(file), Wire) is
                { Name.Length: > 0 } d
                ? d
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static PushesDocument? readPushes(string folder)
    {
        var file = Path.Combine(folder, PushesFile);
        if (!File.Exists(file)) return null;

        try
        {
            var pushes = JsonSerializer.Deserialize<PushesDocument>(File.ReadAllText(file), pushesWire);
            return pushes is null
                ? null
                : pushes with { Sources = new Dictionary<string, PushRecord>(pushes.Sources, StringComparer.OrdinalIgnoreCase) };
        }
        catch (JsonException)
        {
            // Losing the push times is not losing the models: the descriptors still load, on file times.
            return null;
        }
    }

    private Contribution? find(string name, string source)
        => _models.TryGetValue(name, out var sources) ? sources.GetValueOrDefault(source) : null;

    private void remember(EventModelDescriptor descriptor, Contribution contribution)
    {
        if (!_models.TryGetValue(descriptor.Name, out var sources))
        {
            sources = new Dictionary<string, Contribution>(StringComparer.OrdinalIgnoreCase);
            _models[descriptor.Name] = sources;
        }

        sources[contribution.Source] = contribution;
    }

    /// <summary>"event-model" is the default source; "event-model.specs" is source "specs".</summary>
    private static string sourceOf(string fileStem)
    {
        var dot = fileStem.IndexOf('.');
        return dot < 0 ? DefaultSource : fileStem[(dot + 1)..];
    }

    private static string fileNameFor(string source)
        => source.Equals(DefaultSource, StringComparison.OrdinalIgnoreCase)
            ? "event-model.json"
            : $"event-model.{source}.json";

    /// <summary>
    /// A model's folder: its name made safe for a file system, plus a short hash of the exact name,
    /// so two names that sanitize alike (<c>Bank.Account</c>, <c>Bank-Account</c>) never share one.
    /// The folder is only where a model is written; which model a file belongs to is read from the
    /// descriptor inside it.
    /// </summary>
    internal static string FolderNameFor(string modelName)
    {
        var safe = new string(modelName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        if (safe.Length > 60) safe = safe[..60];

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(modelName)))[..8];
        return safe.Length == 0 ? hash : $"{safe}-{hash}";
    }

    private string folderFor(string modelName) => Path.Combine(_dataPath, ModelsFolder, FolderNameFor(modelName));

    private void persist(string modelName, Contribution contribution)
    {
        var folder = folderFor(modelName);
        Directory.CreateDirectory(folder);

        File.WriteAllText(Path.Combine(folder, fileNameFor(contribution.Source)),
            JsonSerializer.Serialize(contribution.Descriptor, Wire));
        writePushes(modelName, folder);
    }

    private void writePushes(string modelName, string folder)
    {
        var sources = _models[modelName].Values.ToDictionary(
            c => c.Source,
            c => new PushRecord(c.PushedAt, c.PushedAtIsFileTime, c.Origin?.Root, c.Origin?.Repository, c.Origin?.Branch));

        File.WriteAllText(Path.Combine(folder, PushesFile),
            JsonSerializer.Serialize(new PushesDocument(modelName, sources), pushesWire));
    }

    /// <summary>
    /// The current model's JSON — the MERGE of every source contributing to the model pushed last —
    /// or null when none has been published. What a bare <c>GET /api/event-model</c> has always
    /// served, and still does.
    /// </summary>
    public string? Read() => Read(null);

    /// <summary>
    /// One model's JSON, the MERGE of every source contributing to it, or null when the console holds
    /// no model of that name. A null <paramref name="name"/> reads the current one.
    /// </summary>
    /// <remarks>
    /// ⚠️ Only sources whose descriptor carries the model's name are merged. Two producers pushing
    /// DIFFERENT model names are two models, not two halves of one, and folding them would invent a
    /// model nobody wrote.
    /// </remarks>
    public string? Read(string? name)
    {
        lock (_gate)
        {
            name ??= _name;
            if (name is null || !_models.TryGetValue(name, out var sources) || sources.Count == 0) return null;

            var parts = sources.Values.Select(c => c.Descriptor).ToArray();
            var merged = parts.Length == 1 ? parts[0] : EventModelDescriptor.Merge(name, parts);
            return JsonSerializer.Serialize(merged, Wire);
        }
    }

    /// <summary>
    /// The name a bare read serves — the model pushed last — or null when none has been published
    /// yet. Issue #169 — the change broadcast names the model it is about, so a console watching two
    /// producers can say which one moved instead of just "something changed".
    /// </summary>
    public string? Name
    {
        get { lock (_gate) return _name; }
    }

    /// <summary>
    /// Every model the console holds (bobcat#799), most recently pushed first, each with its sources:
    /// when each was last pushed and, when the producer said, from which checkout.
    /// </summary>
    public IReadOnlyList<EventModelSummary> Models()
    {
        lock (_gate)
        {
            return _models
                .Where(m => m.Value.Count > 0)
                .Select(m =>
                {
                    var sources = m.Value.Values
                        .OrderByDescending(c => c.PushedAt)
                        .Select(c => new EventModelSourceSummary(c.Source, c.PushedAt, c.PushedAtIsFileTime,
                            c.Origin?.Root, c.Origin?.Repository, c.Origin?.Branch))
                        .ToArray();

                    // The slice count a viewer would see: the merged model's, since slices fold by name.
                    var slices = m.Value.Values.SelectMany(c => c.Descriptor.Slices).Select(s => s.Name)
                        .Distinct(StringComparer.Ordinal).Count();

                    return new EventModelSummary(m.Key, sources[0].PushedAt, slices, sources);
                })
                .OrderByDescending(m => m.LastPushedAt)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .ToArray();
        }
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
    /// keeps its method's identity bound. Returns the names of the models anything was dropped from.
    /// </summary>
    /// <remarks>
    /// Only ever removes. A new specification's slice is a compile-time fact the runner does not
    /// carry, so it reaches the canvas with the next run that publishes the half, as before. Pruning
    /// is not a push, so it leaves the push time alone.
    /// </remarks>
    public IReadOnlyList<string> PruneSpecifications(string suite, IReadOnlyCollection<string> current)
    {
        var source = SpecSourceFor(suite);
        bool keeps(string identity)
            => current.Contains(identity)
               || current.Any(x => x.StartsWith(identity + " (", StringComparison.Ordinal) || x.StartsWith(identity + " [", StringComparison.Ordinal));

        var pruned = new List<string>();

        lock (_gate)
        {
            foreach (var (name, sources) in _models)
            {
                if (!sources.TryGetValue(source, out var contribution)) continue;

                var changed = false;
                var slices = contribution.Descriptor.Slices.Select(slice =>
                {
                    var kept = slice.Specifications.Where(spec => keeps(spec.Identity)).ToArray();
                    if (kept.Length == slice.Specifications.Count) return slice;

                    changed = true;
                    return slice with { Specifications = kept };
                }).ToArray();

                if (!changed) continue;

                var prunedContribution = contribution with { Descriptor = contribution.Descriptor with { Slices = slices } };
                sources[source] = prunedContribution;
                persist(name, prunedContribution);
                pruned.Add(name);
            }
        }

        return pruned;
    }

    /// <summary>
    /// Validate and store a descriptor. The document is round-tripped through the typed
    /// JasperFx <see cref="EventModelDescriptor"/> so an unparseable push is a 400 at the
    /// endpoint rather than a blank canvas later, and so the stored copy is normalized to the
    /// wire shape whatever casing the producer used. Returns the parse failure, or null on
    /// success.
    /// </summary>
    /// <param name="origin">Where the push says it came from (bobcat#532); recorded with it (bobcat#799).</param>
    public string? TryStore(string json, string? source = null, EventModelPushOrigin? origin = null)
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
            // Replaces THIS source of THIS model only — see the type's remarks for why merging on
            // push cannot work. Normalized on the way to disk so the stored copy is the wire shape
            // whatever casing the producer used.
            var contribution = new Contribution(source, descriptor, _time.GetUtcNow(), false, origin);
            remember(descriptor, contribution);
            persist(descriptor.Name, contribution);
            _name = descriptor.Name;
        }

        return null;
    }
}
