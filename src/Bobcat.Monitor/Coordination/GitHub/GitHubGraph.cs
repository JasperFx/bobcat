using System.Text;
using System.Text.Json;

namespace Bobcat.Monitor.Coordination.GitHub;

/// <summary>
/// The pure half of the GitHub poller: builds one GraphQL query per repository covering every
/// issue/PR the plans reference there, and parses the response into observations. GraphQL
/// over REST on purpose — one request per repo per sweep instead of one per issue, and
/// <c>closedByPullRequestsReferences</c> gives the issue→PR closes-linkage (the "addressed by
/// a pull request" status) that REST only surfaces through heavyweight timeline reads.
/// Static and HTTP-free so the wire shapes are testable against canned payloads.
/// </summary>
public static class GitHubGraph
{
    /// <summary>
    /// One repository's worth of references to observe. Owner/name come from parsed plan
    /// documents (validated org/name), numbers from typed node fields — safe to inline.
    /// </summary>
    public static string BuildQuery(string owner, string name, IReadOnlyCollection<int> numbers)
    {
        var query = new StringBuilder();
        query.Append($"query {{ repository(owner: \"{owner}\", name: \"{name}\") {{ ");

        foreach (var number in numbers)
        {
            query.Append($"i{number}: issueOrPullRequest(number: {number}) {{ __typename ...IssueBits ...PrBits }} ");
        }

        query.Append("} } ");
        query.Append("fragment IssueBits on Issue { state title ");
        query.Append("assignees(first: 10) { nodes { login } } ");
        query.Append("labels(first: 20) { nodes { name } } ");
        query.Append("closedByPullRequestsReferences(first: 10) { nodes { number state merged } } } ");
        query.Append("fragment PrBits on PullRequest { state title isDraft merged ");
        query.Append("labels(first: 20) { nodes { name } } }");

        return query.ToString();
    }

    /// <summary>
    /// Parse one repository's response into observations. A null alias (the number matches
    /// nothing in the repo) becomes a "missing" observation rather than silence — the
    /// dashboard owes the user "this plan points at an issue that doesn't exist".
    /// </summary>
    public static IReadOnlyList<GitHubObservation> ParseResponse(
        string owner, string name, string responseJson, DateTimeOffset observedAt)
    {
        using var document = JsonDocument.Parse(responseJson);

        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("repository", out var repository)
            || repository.ValueKind != JsonValueKind.Object)
        {
            // Token without access, repo gone, or a GraphQL-level refusal. The errors array
            // is the only explanation GitHub gives; surface it instead of guessing.
            var reason = document.RootElement.TryGetProperty("errors", out var errors)
                ? errors.ToString()
                : "no data in response";
            throw new InvalidOperationException($"GitHub gave no repository data for {owner}/{name}: {reason}");
        }

        var observations = new List<GitHubObservation>();

        foreach (var property in repository.EnumerateObject())
        {
            if (!property.Name.StartsWith('i') || !int.TryParse(property.Name.AsSpan(1), out var number)) continue;

            var @ref = $"{owner}/{name}#{number}";

            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                observations.Add(new GitHubObservation(
                    @ref, "unknown", "missing", null, [], [], [], Draft: false, observedAt));
                continue;
            }

            observations.Add(parseNode(@ref, property.Value, observedAt));
        }

        return observations;
    }

    private static GitHubObservation parseNode(string @ref, JsonElement node, DateTimeOffset observedAt)
    {
        var typeName = node.TryGetProperty("__typename", out var t) ? t.GetString() : null;
        var title = node.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
        var labels = names(node, "labels", "name");

        if (typeName == "PullRequest")
        {
            var merged = node.TryGetProperty("merged", out var m) && m.GetBoolean();
            // GraphQL PR state is OPEN/CLOSED/MERGED; merged also arrives as CLOSED+merged
            // through some fields, so the boolean wins.
            var state = merged ? "merged" : stateOf(node);

            return new GitHubObservation(
                @ref, "pr", state, title, labels, [], [],
                Draft: node.TryGetProperty("isDraft", out var draft) && draft.GetBoolean(),
                observedAt);
        }

        var closingPrs = new List<ClosingPr>();
        if (node.TryGetProperty("closedByPullRequestsReferences", out var closing)
            && closing.ValueKind == JsonValueKind.Object
            && closing.TryGetProperty("nodes", out var closingNodes))
        {
            foreach (var pr in closingNodes.EnumerateArray())
            {
                if (!pr.TryGetProperty("number", out var n)) continue;
                closingPrs.Add(new ClosingPr(
                    n.GetInt32(),
                    pr.TryGetProperty("state", out var s) ? s.GetString()?.ToLowerInvariant() ?? "open" : "open",
                    pr.TryGetProperty("merged", out var pm) && pm.GetBoolean()));
            }
        }

        return new GitHubObservation(
            @ref, "issue", stateOf(node), title, labels,
            names(node, "assignees", "login"), closingPrs, Draft: false, observedAt);
    }

    private static string stateOf(JsonElement node)
        => node.TryGetProperty("state", out var state)
            ? state.GetString()?.ToLowerInvariant() ?? "open"
            : "open";

    private static IReadOnlyList<string> names(JsonElement node, string collection, string field)
    {
        if (!node.TryGetProperty(collection, out var outer)
            || outer.ValueKind != JsonValueKind.Object
            || !outer.TryGetProperty("nodes", out var nodes))
        {
            return [];
        }

        return nodes.EnumerateArray()
            .Select(x => x.TryGetProperty(field, out var value) ? value.GetString() : null)
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();
    }
}
