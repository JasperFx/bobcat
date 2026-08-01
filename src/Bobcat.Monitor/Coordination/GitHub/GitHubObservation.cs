namespace Bobcat.Monitor.Coordination.GitHub;

/// <summary>A pull request GitHub reports as closing an observed issue.</summary>
public record ClosingPr(int Number, string State, bool Merged);

/// <summary>
/// What GitHub last said about one issue or pull request — an OBSERVATION, never an
/// assertion (docs/agent-coordination-design.md). Ref is "org/repo#n", the identity the plan
/// nodes share. State is lower-case wire vocabulary: open, closed, merged — or "missing"
/// when the reference points at nothing, because a plan naming a nonexistent issue is a
/// wiring mistake that must surface, not a row to skip.
/// </summary>
public record GitHubObservation(
    string Ref,
    string Kind,
    string State,
    string? Title,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Assignees,
    IReadOnlyList<ClosingPr> ClosingPrs,
    bool Draft,
    DateTimeOffset ObservedAt);

/// <summary>
/// The monitor's memory of GitHub state, keyed by "org/repo#n". A snapshot cache on purpose:
/// when the SQLite event store lands, <see cref="Upsert"/>'s change detection is where
/// observation events get emitted — the seam is the "did anything change" answer, which is
/// why it compares everything except the observation timestamp.
/// </summary>
public sealed class GitHubStatusCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, GitHubObservation> _observations = new();

    /// <summary>True when this observation differs from the last one (or is the first).</summary>
    public bool Upsert(GitHubObservation observation)
    {
        lock (_gate)
        {
            var changed = !_observations.TryGetValue(observation.Ref, out var last)
                          || !sameObservation(last, observation);
            _observations[observation.Ref] = observation;
            return changed;
        }
    }

    public GitHubObservation? Find(string @ref)
    {
        lock (_gate) return _observations.GetValueOrDefault(@ref);
    }

    public IReadOnlyList<GitHubObservation> All()
    {
        lock (_gate) return _observations.Values.ToList();
    }

    private static bool sameObservation(GitHubObservation a, GitHubObservation b)
        => a.Kind == b.Kind
           && a.State == b.State
           && a.Title == b.Title
           && a.Draft == b.Draft
           && a.Labels.SequenceEqual(b.Labels)
           && a.Assignees.SequenceEqual(b.Assignees)
           && a.ClosingPrs.SequenceEqual(b.ClosingPrs);
}
