using System.Text.Json;

namespace Bobcat.Monitor.Coordination.NuGet;

/// <summary>
/// A publish node's "done" needs a reference point: the plan declares a bump KIND, so the
/// watcher must know what the package's latest version was when watching began, or a monitor
/// restart could not tell "already published" from "not yet". Baselines are keyed per
/// plan+node (two plans publishing the same package at different times need different
/// baselines), captured once by the poller on first observation, and persisted to a JSON
/// file so they survive restarts. Empty string means "the package did not exist at capture"
/// — a first-ever publish, where any version satisfies the bump.
///
/// This file is the pragmatic bridge until the SQLite event store: a baseline is really
/// "the observation stream started here", and it becomes exactly that when the stream is real.
/// </summary>
public sealed class NuGetBaselineStore
{
    private readonly string _file;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _baselines;

    public NuGetBaselineStore(string? dataPath = null)
    {
        var directory = dataPath
                        ?? Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".bobcat", "monitor", "coordination");

        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "nuget-baselines.json");
        _baselines = load(_file);
    }

    private static Dictionary<string, string> load(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new();
            }
        }
        catch
        {
            // A torn or hand-mangled file re-baselines rather than killing the monitor.
        }

        return new Dictionary<string, string>();
    }

    private static string keyFor(string plan, string nodeId) => $"{plan}/{nodeId}";

    /// <summary>Null = never captured. Empty = captured, package absent at capture.</summary>
    public string? TryGet(string plan, string nodeId)
    {
        lock (_gate) return _baselines.GetValueOrDefault(keyFor(plan, nodeId));
    }

    /// <summary>First capture wins — a baseline is a fact about when watching began, and
    /// re-capturing it against a later feed state would erase the very change being watched for.</summary>
    public void Capture(string plan, string nodeId, string baselineVersion)
    {
        lock (_gate)
        {
            if (!_baselines.TryAdd(keyFor(plan, nodeId), baselineVersion)) return;

            try
            {
                File.WriteAllText(_file, JsonSerializer.Serialize(
                    _baselines, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Persistence is best-effort; the in-memory baseline still governs this run.
            }
        }
    }
}
