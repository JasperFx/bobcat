namespace Bobcat.Monitor.Coordination;

public record NodeClaim(
    string Plan,
    string NodeId,
    string Agent,
    DateTimeOffset ClaimedAt,
    DateTimeOffset ExpiresAt,
    string? Note);

public record ClaimResult(NodeClaim? Claim, NodeClaim? Conflict)
{
    public bool Succeeded => Claim is not null;
}

/// <summary>
/// Leased agent claims on plan nodes — the ASSERTED side of the coordination context, and
/// deliberately lease-based: the derivation-first rule says a crashed agent's work must
/// never render "in progress" forever, and for an assertion nobody can observe, expiry is
/// the only honest mechanism. The default lease is 30 minutes; report_node renews it, so a
/// live agent holds its claim indefinitely and a dead one loses it by construction.
///
/// The GitHub-native path (the agent:working label, an assignee) stays what it always was —
/// observed by the poller, owned by whoever set it. This store never writes to GitHub.
///
/// In-memory on purpose: a lease measured in minutes does not need to survive a monitor
/// restart, and durable claims become events when the SQLite event store lands. The one
/// note kept per claim is the seed of the session-memory capture layer, not a history —
/// history is the event store's job.
/// </summary>
public sealed class ClaimStore
{
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MaxLease = TimeSpan.FromHours(4);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, NodeClaim> _claims = new();

    private static string keyFor(string plan, string nodeId) => $"{plan}/{nodeId}";

    /// <summary>Claim or renew. Refused when another agent holds an unexpired lease — the
    /// conflict is returned so the caller can say WHO to the agent.</summary>
    public ClaimResult TryClaim(string plan, string nodeId, string agent, TimeSpan lease)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var existing = findLive(plan, nodeId, now);
            if (existing is not null && existing.Agent != agent)
            {
                return new ClaimResult(null, existing);
            }

            var claim = new NodeClaim(
                plan, nodeId, agent,
                existing?.ClaimedAt ?? now,
                now + clamp(lease),
                existing?.Note);

            _claims[keyFor(plan, nodeId)] = claim;
            return new ClaimResult(claim, null);
        }
    }

    /// <summary>Attach/replace the claim's note and renew the lease. Only the holder can.</summary>
    public NodeClaim? Report(string plan, string nodeId, string agent, string? note, TimeSpan lease)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var existing = findLive(plan, nodeId, now);
            if (existing is null || existing.Agent != agent) return null;

            var renewed = existing with
            {
                ExpiresAt = now + clamp(lease),
                Note = note ?? existing.Note
            };
            _claims[keyFor(plan, nodeId)] = renewed;
            return renewed;
        }
    }

    /// <summary>Only the holder can release; anyone else's stale claim expires on its own.</summary>
    public bool Release(string plan, string nodeId, string agent)
    {
        lock (_gate)
        {
            var existing = findLive(plan, nodeId, DateTimeOffset.UtcNow);
            if (existing is null || existing.Agent != agent) return false;

            _claims.Remove(keyFor(plan, nodeId));
            return true;
        }
    }

    /// <summary>Null when unclaimed or expired — expiry IS the release for a dead agent.</summary>
    public NodeClaim? Find(string plan, string nodeId)
    {
        lock (_gate) return findLive(plan, nodeId, DateTimeOffset.UtcNow);
    }

    private NodeClaim? findLive(string plan, string nodeId, DateTimeOffset now)
    {
        var key = keyFor(plan, nodeId);
        if (!_claims.TryGetValue(key, out var claim)) return null;

        if (claim.ExpiresAt <= now)
        {
            _claims.Remove(key);
            return null;
        }

        return claim;
    }

    private static TimeSpan clamp(TimeSpan lease)
        => lease <= TimeSpan.Zero ? DefaultLease : lease > MaxLease ? MaxLease : lease;
}
