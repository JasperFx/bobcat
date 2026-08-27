using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Events.EventModeling;

namespace Bobcat.Console.EventModel;

/// <summary>
/// The console's copy of the current Event Model descriptor (issue #108) — one JSON document,
/// latest push wins, persisted beside the run archives so it survives a restart. Deliberately
/// not per-run state: the descriptor is design-time truth (from Wolverine's <c>event-model</c>
/// export or a Bobcat spec assembly's generated source) and run evidence joins onto it by spec
/// identity, so it lives on its own file rather than in any <c>RunProjection</c>.
/// </summary>
public sealed class EventModelStore
{
    /// <summary>
    /// The wire shape both viewers agree on: camelCase members, PascalCase enum values —
    /// exactly what <c>@jasperfx/event-model-vue</c>'s hand-written TypeScript mirror types
    /// (its contract spec pins the enum members), and what JasperFx's own wire round-trip
    /// tests exercise. Reading is enum-case-insensitive, so a producer serializing camelCase
    /// enum values is normalized rather than rejected.
    /// </summary>
    public static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(namingPolicy: null) }
    };

    private readonly string _file;
    private readonly object _gate = new();
    private string? _json;
    private string? _name;

    public EventModelStore(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        _file = Path.Combine(dataPath, "event-model.json");
        if (!File.Exists(_file)) return;

        _json = File.ReadAllText(_file);
        // #169 — recover the name across a restart too, so the first broadcast after one is not
        // anonymous. Best-effort: a corrupt file on disk must not stop the console booting, and
        // Read() has always handed the bytes back untouched whatever they are.
        try
        {
            _name = JsonSerializer.Deserialize<EventModelDescriptor>(_json, Wire)?.Name;
        }
        catch (JsonException)
        {
            // Leave the name null; the document itself is still served exactly as before.
        }
    }

    /// <summary>The stored descriptor's JSON, or null when none has been published.</summary>
    public string? Read()
    {
        lock (_gate) return _json;
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
    /// Validate and store a descriptor. The document is round-tripped through the typed
    /// JasperFx <see cref="EventModelDescriptor"/> so an unparseable push is a 400 at the
    /// endpoint rather than a blank canvas later, and so the stored copy is normalized to the
    /// wire shape whatever casing the producer used. Returns the parse failure, or null on
    /// success.
    /// </summary>
    public string? TryStore(string json)
    {
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

        var normalized = JsonSerializer.Serialize(descriptor, Wire);
        lock (_gate)
        {
            File.WriteAllText(_file, normalized);
            _json = normalized;
            _name = descriptor.Name;
        }

        return null;
    }
}
