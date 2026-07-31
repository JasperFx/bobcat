using System.Text.Json;
using Bobcat.Monitor.Contracts;

namespace Bobcat.Monitor.Runs;

/// <summary>
/// The monitor's memory: every ingested event is folded into a per-run
/// <see cref="RunProjection"/> AND appended verbatim to a per-run NDJSON file, so a run can be
/// exported (CTRF/JUnit), replayed for debugging, or inspected after the monitor restarts.
/// The raw event stream is the source of truth; the projection is a cache over it.
/// </summary>
/// <remarks>
/// Ejecting a run (<see cref="Remove"/>) drops it from memory and closes its file — it never
/// deletes the NDJSON from disk. Data location: <c>Monitor:DataPath</c> configuration, then the
/// <c>BOBCAT_MONITOR_DATA</c> environment variable, then <c>~/.bobcat/monitor/runs</c>.
/// </remarks>
public sealed class MonitorRunRegistry : IDisposable
{
    public const string DataPathVariable = "BOBCAT_MONITOR_DATA";

    private static readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _dataPath;
    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly Lock _gate = new();

    private sealed class Entry
    {
        public required RunProjection Projection { get; init; }
        public required StreamWriter Writer { get; init; }
    }

    public MonitorRunRegistry(string? dataPath = null)
    {
        _dataPath = dataPath
                    ?? Environment.GetEnvironmentVariable(DataPathVariable)
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".bobcat", "monitor", "runs");

        Directory.CreateDirectory(_dataPath);
    }

    public string DataPath => _dataPath;

    public string ArchiveFileFor(Guid runId) => Path.Combine(_dataPath, $"{runId}.ndjson");

    /// <summary>Fold a batch into the projections and append each event to its run's archive.</summary>
    public void Record(IReadOnlyList<MonitorEvent> events)
    {
        lock (_gate)
        {
            var touched = new HashSet<Guid>();

            foreach (var @event in events)
            {
                var entry = ensureEntry(@event.RunId);
                entry.Projection.Apply(@event);
                // Declared type MonitorEvent keeps the polymorphic "type" discriminator on the
                // line, so an archived line is byte-for-byte re-ingestable.
                entry.Writer.WriteLine(JsonSerializer.Serialize(@event, serializerOptions));
                touched.Add(@event.RunId);
            }

            foreach (var runId in touched)
            {
                _entries[runId].Writer.Flush();
            }
        }
    }

    public IReadOnlyList<RunProjection> All()
    {
        lock (_gate) return _entries.Values.Select(e => e.Projection).ToList();
    }

    public RunProjection? Find(Guid runId)
    {
        lock (_gate) return _entries.TryGetValue(runId, out var entry) ? entry.Projection : null;
    }

    /// <summary>
    /// Eject: forget the run and close its archive file. The NDJSON stays on disk — ejecting
    /// is a dashboard operation, not a data deletion.
    /// </summary>
    public bool Remove(Guid runId)
    {
        lock (_gate)
        {
            if (!_entries.Remove(runId, out var entry)) return false;
            entry.Writer.Dispose();
            return true;
        }
    }

    /// <summary>Raw archive bytes for the NDJSON export. Flushes first so the tail is current.</summary>
    public byte[]? ReadArchive(Guid runId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(runId, out var entry)) entry.Writer.Flush();
        }

        var file = ArchiveFileFor(runId);
        return File.Exists(file) ? File.ReadAllBytes(file) : null;
    }

    private Entry ensureEntry(Guid runId)
    {
        if (!_entries.TryGetValue(runId, out var entry))
        {
            // Append mode: a run re-appearing after an eject (or a monitor restart mid-run)
            // continues its existing archive rather than truncating it.
            var stream = new FileStream(ArchiveFileFor(runId), FileMode.Append, FileAccess.Write, FileShare.Read);
            entry = new Entry
            {
                Projection = new RunProjection(runId),
                Writer = new StreamWriter(stream)
            };
            _entries[runId] = entry;
        }

        return entry;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                try { entry.Writer.Dispose(); }
                catch { }
            }

            _entries.Clear();
        }
    }
}
