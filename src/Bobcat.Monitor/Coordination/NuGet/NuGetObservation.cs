namespace Bobcat.Monitor.Coordination.NuGet;

/// <summary>
/// What a feed last said about one package — or, when Error is set, why the watcher couldn't
/// ask (an unconfigured feed name is the wiring fault a plan must surface). Same observed-
/// never-asserted stance as <see cref="GitHub.GitHubObservation"/>.
/// </summary>
public record NuGetObservation(
    string Feed,
    string Package,
    IReadOnlyList<string> Versions,
    string? Error,
    DateTimeOffset ObservedAt)
{
    public static string KeyFor(string feed, string package) => $"{feed}/{package.ToLowerInvariant()}";
}

/// <summary>
/// Snapshot cache keyed by feed + package. Like <see cref="GitHub.GitHubStatusCache"/>, the
/// Upsert change detection is where observation events get emitted once the SQLite event
/// store lands.
/// </summary>
public sealed class NuGetStatusCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, NuGetObservation> _observations = new();

    public bool Upsert(NuGetObservation observation)
    {
        var key = NuGetObservation.KeyFor(observation.Feed, observation.Package);
        lock (_gate)
        {
            var changed = !_observations.TryGetValue(key, out var last)
                          || last.Error != observation.Error
                          || !last.Versions.SequenceEqual(observation.Versions);
            _observations[key] = observation;
            return changed;
        }
    }

    public NuGetObservation? Find(string feed, string package)
    {
        lock (_gate) return _observations.GetValueOrDefault(NuGetObservation.KeyFor(feed, package));
    }
}
