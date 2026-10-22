using System.Text.Json;

namespace Bobcat.Supervisor;

/// <summary>
/// The gate's own record of how long each unit took on this machine — the predictions that
/// order the next run (bobcat#729). A JSON map of unit name to its last few wall clocks, in
/// milliseconds. Nothing reads it but the gate; nothing writes it but the gate.
/// </summary>
/// <remarks>
/// Deliberately not the committed <c>TestLedger</c>: that is per test and shared across machines,
/// this is per unit and per machine, and a unit's wall clock on an eight-core laptop says nothing
/// about a four-core runner. The prediction is the mean of the kept observations, so one slow run
/// does not reorder the next gate by itself.
/// </remarks>
public sealed class GateDurations
{
    private const int kept = 5;
    private static readonly JsonSerializerOptions options = new() { WriteIndented = true };

    private readonly Dictionary<string, List<long>> _observations;

    private GateDurations(Dictionary<string, List<long>> observations) => _observations = observations;

    public static GateDurations Empty() => new(new Dictionary<string, List<long>>(StringComparer.Ordinal));

    /// <summary>Load, or empty when the file is missing or unreadable — a prediction must never fail a gate.</summary>
    public static GateDurations Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Empty();

            var read = JsonSerializer.Deserialize<Dictionary<string, List<long>>>(File.ReadAllText(path));
            return read is null
                ? Empty()
                : new GateDurations(new Dictionary<string, List<long>>(read, StringComparer.Ordinal));
        }
        catch
        {
            return Empty();
        }
    }

    /// <summary>What the record predicts for <paramref name="unit"/>: null when it has never run here.</summary>
    public TimeSpan? Predict(GateUnit unit) => Predict(unit.Name);

    public TimeSpan? Predict(string unitName)
        => _observations.TryGetValue(unitName, out var runs) && runs.Count > 0
            ? TimeSpan.FromMilliseconds(runs.Average())
            : null;

    /// <summary>Record one run of a unit, keeping the newest few.</summary>
    public GateDurations Record(string unitName, TimeSpan duration)
    {
        if (!_observations.TryGetValue(unitName, out var runs)) _observations[unitName] = runs = [];

        runs.Add((long)duration.TotalMilliseconds);
        if (runs.Count > kept) runs.RemoveRange(0, runs.Count - kept);
        return this;
    }

    /// <summary>Write, creating the directory. Swallows nothing: a caller that asked for a record wants to know it was not kept.</summary>
    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null) Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(_observations, options));
    }

    /// <summary>The unit names with a record.</summary>
    public IReadOnlyCollection<string> Units => _observations.Keys;
}
