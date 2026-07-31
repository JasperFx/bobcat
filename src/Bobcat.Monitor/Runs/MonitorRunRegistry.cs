using System.Text.Json;
using Bobcat.Monitor.Contracts;

namespace Bobcat.Monitor.Runs;

/// <summary>
/// The monitor's memory: every ingested event is folded into a per-run
/// <see cref="RunProjection"/> AND appended verbatim to a per-run NDJSON file, so a run can be
/// exported (CTRF/JUnit), replayed for debugging, or inspected after the monitor restarts.
/// The raw event stream is the source of truth; the projection is a cache over it — which is
/// exactly why a restarted monitor can rebuild itself by replaying the archives (see the
/// constructor).
/// </summary>
/// <remarks>
/// Ejecting a run (<see cref="Remove"/>) drops it from memory and moves its archive into the
/// <c>ejected/</c> subfolder — never deleted, but excluded from boot rehydration so an eject
/// survives a monitor restart. Data location: <c>Monitor:DataPath</c> configuration, then the
/// <c>BOBCAT_MONITOR_DATA</c> environment variable, then <c>~/.bobcat/monitor/runs</c>.
/// Retention: <c>Monitor:RetentionDays</c>, then <c>BOBCAT_MONITOR_RETENTION_DAYS</c>, then
/// 14 days; see <see cref="SweepAging"/> for what aging means.
/// </remarks>
public sealed class MonitorRunRegistry : IDisposable
{
    public const string DataPathVariable = "BOBCAT_MONITOR_DATA";
    public const string RetentionVariable = "BOBCAT_MONITOR_RETENTION_DAYS";
    public const string EjectedFolder = "ejected";

    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(14);

    private static readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _dataPath;
    private readonly TimeSpan _retention;
    private readonly Dictionary<Guid, Entry> _entries = new();
    private readonly Lock _gate = new();

    private sealed class Entry
    {
        public required RunProjection Projection { get; init; }

        /// <summary>
        /// Opened lazily on the first NEW event — boot rehydration must not hold an open
        /// append handle for every historical run that will never publish again.
        /// </summary>
        public StreamWriter? Writer { get; set; }
    }

    public MonitorRunRegistry(string? dataPath = null, TimeSpan? retention = null)
    {
        _dataPath = dataPath
                    ?? Environment.GetEnvironmentVariable(DataPathVariable)
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".bobcat", "monitor", "runs");

        // Zero or negative disables aging; the on/off switch and the length are one knob.
        _retention = retention
                     ?? retentionFromEnvironment()
                     ?? DefaultRetention;

        Directory.CreateDirectory(_dataPath);
        // Age before rehydrating, so a long-dead archive is never loaded just to be swept.
        SweepAging();
        rehydrate();
    }

    private static TimeSpan? retentionFromEnvironment()
        => double.TryParse(Environment.GetEnvironmentVariable(RetentionVariable), out var days)
            ? TimeSpan.FromDays(days)
            : null;

    /// <summary>
    /// Replays every non-ejected archive back into a projection, so a monitor restart forgets
    /// nothing. A rehydrated run with no terminal RunFinished is marked
    /// <see cref="RunProjection.Orphaned"/> — its publisher is gone, and rendering it as
    /// "still running" forever would be a lie. Any new event for the run un-orphans it (the
    /// publisher survived the monitor's restart).
    /// </summary>
    private void rehydrate()
    {
        foreach (var file in Directory.EnumerateFiles(_dataPath, "*.ndjson"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var runId)) continue;

            var projection = new RunProjection(runId);
            var replayed = 0;

            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var @event = JsonSerializer.Deserialize<MonitorEvent>(line, serializerOptions);
                    if (@event == null) continue;
                    projection.Apply(@event);
                    replayed++;
                }
                catch
                {
                    // A torn tail line (monitor killed mid-write) must not sink the whole run.
                }
            }

            if (replayed == 0) continue;

            projection.Orphaned = !projection.Finished;
            _entries[runId] = new Entry { Projection = projection };
        }
    }

    public string DataPath => _dataPath;

    /// <summary>Zero or negative means aging is disabled.</summary>
    public TimeSpan Retention => _retention;

    /// <summary>
    /// Age the archive directory: any archive whose last write is older than the retention
    /// period is retired. A stale live archive is ejected exactly like a manual
    /// <see cref="Remove"/> (dropped from the dashboard, moved to <c>ejected/</c>); a stale
    /// ejected archive is deleted. Moving preserves the file's timestamp, so a run aged out
    /// of the live folder falls to the delete pass in the same sweep — the grace period in
    /// <c>ejected/</c> exists for runs ejected by hand, not as a second countdown. The mtime
    /// is the aging clock on purpose: every ingested event (heartbeats included) appends to
    /// the archive, so a file untouched for the whole retention period has had a dead
    /// publisher for exactly that long. Runs on construction (before rehydrate, so a
    /// long-dead archive is never loaded just to be swept) and periodically from
    /// <see cref="ArchiveRetentionService"/>.
    /// </summary>
    public (int Ejected, int Deleted) SweepAging()
    {
        if (_retention <= TimeSpan.Zero) return (0, 0);

        var cutoff = DateTime.UtcNow - _retention;
        var ejected = 0;
        var deleted = 0;

        lock (_gate)
        {
            foreach (var file in Directory.EnumerateFiles(_dataPath, "*.ndjson"))
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;

                if (Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var runId)
                    && _entries.ContainsKey(runId))
                {
                    // System.Threading.Lock is reentrant, so reusing the one eject code path
                    // from under the sweep's own lock is safe.
                    if (Remove(runId)) ejected++;
                }
                else
                {
                    // Boot-time sweep (nothing rehydrated yet) or a file nobody tracks.
                    try
                    {
                        Directory.CreateDirectory(Path.Combine(_dataPath, EjectedFolder));
                        File.Move(file, Path.Combine(_dataPath, EjectedFolder, Path.GetFileName(file)), overwrite: true);
                        ejected++;
                    }
                    catch
                    {
                        // A file that can't be moved right now ages again on the next sweep.
                    }
                }
            }

            var ejectedDir = Path.Combine(_dataPath, EjectedFolder);
            if (Directory.Exists(ejectedDir))
            {
                foreach (var file in Directory.EnumerateFiles(ejectedDir, "*.ndjson"))
                {
                    if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;

                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch
                    {
                        // Same rule: failure to delete now is retried by the next sweep.
                    }
                }
            }
        }

        return (ejected, deleted);
    }

    public string ArchiveFileFor(Guid runId) => Path.Combine(_dataPath, $"{runId}.ndjson");

    public string EjectedFileFor(Guid runId) => Path.Combine(_dataPath, EjectedFolder, $"{runId}.ndjson");

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
                // A live event means a live publisher — a rehydrated run is orphaned no more.
                entry.Projection.Orphaned = false;

                entry.Writer ??= openWriter(@event.RunId);
                // Declared type MonitorEvent keeps the polymorphic "type" discriminator on the
                // line, so an archived line is byte-for-byte re-ingestable.
                entry.Writer.WriteLine(JsonSerializer.Serialize(@event, serializerOptions));
                touched.Add(@event.RunId);
            }

            foreach (var runId in touched)
            {
                _entries[runId].Writer?.Flush();
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
    /// Shape one run into a result UNDER the registry's gate. Projections mutate while
    /// ingestion runs, so a reader enumerating scenarios outside the lock can see a torn
    /// collection — exports and MCP tools read through here instead.
    /// </summary>
    public T? Read<T>(Guid runId, Func<RunProjection, T> reader) where T : class
    {
        lock (_gate)
        {
            return _entries.TryGetValue(runId, out var entry) ? reader(entry.Projection) : null;
        }
    }

    /// <summary>All-runs variant of <see cref="Read{T}"/> — same torn-read rationale.</summary>
    public T ReadAll<T>(Func<IReadOnlyList<RunProjection>, T> reader)
    {
        lock (_gate)
        {
            return reader(_entries.Values.Select(e => e.Projection).ToList());
        }
    }

    /// <summary>
    /// Eject: forget the run and move its archive to <c>ejected/</c>. The data survives on
    /// disk, but boot rehydration skips it — an eject would otherwise silently undo itself on
    /// the next monitor restart.
    /// </summary>
    public bool Remove(Guid runId)
    {
        lock (_gate)
        {
            if (!_entries.Remove(runId, out var entry)) return false;
            entry.Writer?.Dispose();

            try
            {
                var archive = ArchiveFileFor(runId);
                if (File.Exists(archive))
                {
                    Directory.CreateDirectory(Path.Combine(_dataPath, EjectedFolder));
                    File.Move(archive, EjectedFileFor(runId), overwrite: true);
                }
            }
            catch
            {
                // Losing the move is not worth failing the eject over; worst case the run
                // rehydrates once more on the next restart.
            }

            return true;
        }
    }

    /// <summary>Raw archive bytes for the NDJSON export. Flushes first so the tail is current.</summary>
    public byte[]? ReadArchive(Guid runId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(runId, out var entry)) entry.Writer?.Flush();
        }

        var file = ArchiveFileFor(runId);
        return File.Exists(file) ? File.ReadAllBytes(file) : null;
    }

    private Entry ensureEntry(Guid runId)
    {
        if (!_entries.TryGetValue(runId, out var entry))
        {
            entry = new Entry { Projection = new RunProjection(runId) };
            _entries[runId] = entry;
        }

        return entry;
    }

    private FileStream openStream(Guid runId)
        // Append mode: a run continuing after a monitor restart (or re-appearing after an
        // eject) extends its archive rather than truncating it.
        => new(ArchiveFileFor(runId), FileMode.Append, FileAccess.Write, FileShare.Read);

    private StreamWriter openWriter(Guid runId) => new(openStream(runId));

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                try { entry.Writer?.Dispose(); }
                catch { }
            }

            _entries.Clear();
        }
    }
}
