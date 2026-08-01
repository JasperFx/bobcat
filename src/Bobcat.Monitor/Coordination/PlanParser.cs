using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bobcat.Monitor.Coordination;

/// <summary>
/// Everything wrong with the document, gathered in one pass — the same philosophy as Bobcat's
/// assertion failures. Document is null unless Errors is empty.
/// </summary>
public record PlanParseResult(PlanDocument? Document, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Document is not null;
}

/// <summary>
/// Parses and validates plan documents (docs/agent-coordination-design.md). Strict on
/// purpose: unknown keys, duplicate keys, unknown kinds, dangling or cyclic dependencies are
/// all errors — these files are hand- or agent-authored, and a silently ignored typo in
/// `depends_on` is a dependency edge that never existed.
/// </summary>
public static partial class PlanParser
{
    public static PlanParseResult Parse(string yaml)
    {
        PlanDto dto;
        try
        {
            dto = deserializer.Deserialize<PlanDto>(yaml);
        }
        catch (YamlException e)
        {
            // Unmatched/duplicate keys and malformed YAML land here. The innermost message
            // names the offending key; the outer one carries the document position.
            var detail = e.InnerException?.Message ?? e.Message;

            // YamlDotNet phrases an unmatched key as "Property 'x' not found on type '<DTO>'"
            // — reshape it so the wire never names an internal type.
            var unknownKey = UnknownKeyRegex().Match(detail);
            if (unknownKey.Success) detail = $"unknown key '{unknownKey.Groups[1].Value}'";

            return new PlanParseResult(null, [$"invalid plan document at {e.Start}: {detail}"]);
        }

        if (dto is null) return new PlanParseResult(null, ["the document is empty"]);

        var errors = new List<string>();

        if (dto.Schema is null) errors.Add("'schema' is required (the only version is 1)");
        else if (dto.Schema != 1) errors.Add($"unknown schema version {dto.Schema} (the only version is 1)");

        if (string.IsNullOrWhiteSpace(dto.Plan)) errors.Add("'plan' (the plan's slug) is required");
        else if (!SlugRegex().IsMatch(dto.Plan)) errors.Add($"plan slug '{dto.Plan}' must be lower-case kebab (a-z, 0-9, '-')");

        if (dto.Anchor is not null && !IssueRefRegex().IsMatch(dto.Anchor))
            errors.Add($"anchor '{dto.Anchor}' must look like org/repo#123");

        var repos = dto.Repos ?? new Dictionary<string, string>();
        foreach (var (alias, repo) in repos)
        {
            if (!RepoRegex().IsMatch(repo))
                errors.Add($"repo alias '{alias}': '{repo}' must look like org/name");
        }

        if (dto.Nodes is not { Count: > 0 })
        {
            errors.Add("a plan needs at least one node");
            return new PlanParseResult(null, errors);
        }

        var nodes = new List<PlanNode>();
        var seenIds = new HashSet<string>();
        foreach (var raw in dto.Nodes)
        {
            var node = buildNode(raw, repos, errors);
            if (node is null) continue;

            if (!seenIds.Add(node.Id)) errors.Add($"node id '{node.Id}' is declared more than once");
            nodes.Add(node);
        }

        validateEdges(nodes, seenIds, errors);

        if (errors.Count > 0) return new PlanParseResult(null, errors);

        var ordered = sortByDependencies(nodes, errors);
        if (errors.Count > 0) return new PlanParseResult(null, errors);

        var document = new PlanDocument
        {
            Schema = dto.Schema!.Value,
            Plan = dto.Plan!,
            Title = string.IsNullOrWhiteSpace(dto.Title) ? dto.Plan! : dto.Title!,
            Anchor = dto.Anchor,
            Repos = repos,
            Nodes = nodes,
            InDependencyOrder = ordered
        };

        return new PlanParseResult(document, []);
    }

    private static PlanNode? buildNode(NodeDto raw, IReadOnlyDictionary<string, string> repos, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(raw.Id))
        {
            errors.Add("every node needs an 'id'");
            return null;
        }

        var where = $"node '{raw.Id}'";

        if (!SlugRegex().IsMatch(raw.Id))
            errors.Add($"{where}: id must be lower-case kebab (a-z, 0-9, '-')");

        if (string.IsNullOrWhiteSpace(raw.Kind))
        {
            errors.Add($"{where}: 'kind' is required ({PlanWire.KindNames})");
            return null;
        }

        if (!PlanWire.TryKind(raw.Kind, out var kind))
        {
            errors.Add($"{where}: unknown kind '{raw.Kind}' ({PlanWire.KindNames})");
            return null;
        }

        var repo = resolveRepo(raw.Repo, repos, where, errors);
        var merge = parseMerge(raw.Merge, where, errors);
        var bump = parseBump(raw.Bump, where, errors);

        // What each kind requires — and just as deliberately, what it refuses. A publish node
        // carrying a repo, or a test gate carrying a package, is a misunderstanding of the
        // model that should surface at parse time, not render as a half-sensible node.
        switch (kind)
        {
            case PlanNodeKind.Issue:
            case PlanNodeKind.PullRequest:
                if (raw.Repo is null) errors.Add($"{where}: {raw.Kind} nodes need a 'repo'");
                if (kind == PlanNodeKind.Issue && raw.Pr is not null) errors.Add($"{where}: 'pr' does not apply to issue nodes");
                if (kind == PlanNodeKind.PullRequest && raw.Issue is not null) errors.Add($"{where}: 'issue' does not apply to pr nodes");
                refuse(where, errors, ("package", raw.Package), ("feed", raw.Feed), ("bump", raw.Bump));
                merge ??= MergePolicy.ManualReview;
                break;

            case PlanNodeKind.Publish:
                if (string.IsNullOrWhiteSpace(raw.Package)) errors.Add($"{where}: publish nodes need a 'package'");
                if (raw.Bump is null) errors.Add($"{where}: publish nodes need a 'bump' ({PlanWire.BumpNames})");
                refuse(where, errors, ("repo", raw.Repo), ("issue", raw.Issue?.ToString()), ("pr", raw.Pr?.ToString()), ("merge", raw.Merge));
                break;

            case PlanNodeKind.Consume:
                if (raw.Repo is null) errors.Add($"{where}: consume nodes need a 'repo'");
                if (string.IsNullOrWhiteSpace(raw.Package)) errors.Add($"{where}: consume nodes need a 'package'");
                refuse(where, errors, ("feed", raw.Feed), ("bump", raw.Bump), ("issue", raw.Issue?.ToString()), ("pr", raw.Pr?.ToString()), ("merge", raw.Merge));
                break;

            case PlanNodeKind.TestRunGate:
                refuse(where, errors,
                    ("repo", raw.Repo), ("issue", raw.Issue?.ToString()), ("pr", raw.Pr?.ToString()),
                    ("merge", raw.Merge), ("package", raw.Package), ("feed", raw.Feed), ("bump", raw.Bump));
                break;
        }

        return new PlanNode
        {
            Id = raw.Id,
            Kind = kind,
            Title = string.IsNullOrWhiteSpace(raw.Title) ? raw.Id : raw.Title!,
            Repo = repo,
            Issue = raw.Issue,
            PullRequest = raw.Pr,
            Merge = merge,
            Package = raw.Package,
            Feed = kind == PlanNodeKind.Publish ? raw.Feed ?? "nuget.org" : null,
            Bump = bump,
            DependsOn = raw.DependsOn ?? []
        };
    }

    private static string? resolveRepo(string? value, IReadOnlyDictionary<string, string> repos, string where, List<string> errors)
    {
        if (value is null) return null;

        // "org/name" is used literally; anything else must be a declared alias.
        if (value.Contains('/'))
        {
            if (!RepoRegex().IsMatch(value)) errors.Add($"{where}: repo '{value}' must look like org/name");
            return value;
        }

        if (repos.TryGetValue(value, out var resolved)) return resolved;

        errors.Add($"{where}: repo alias '{value}' is not declared under 'repos'");
        return null;
    }

    private static MergePolicy? parseMerge(string? value, string where, List<string> errors)
    {
        if (value is null) return null;
        if (PlanWire.TryMerge(value, out var merge)) return merge;

        errors.Add($"{where}: unknown merge policy '{value}' ({PlanWire.MergeNames})");
        return null;
    }

    private static BumpKind? parseBump(string? value, string where, List<string> errors)
    {
        if (value is null) return null;
        if (PlanWire.TryBump(value, out var bump)) return bump;

        errors.Add($"{where}: unknown bump '{value}' ({PlanWire.BumpNames})");
        return null;
    }

    private static void refuse(string where, List<string> errors, params (string Name, string? Value)[] fields)
    {
        foreach (var (name, value) in fields)
        {
            if (value is not null) errors.Add($"{where}: '{name}' does not apply to this node kind");
        }
    }

    private static void validateEdges(List<PlanNode> nodes, HashSet<string> ids, List<string> errors)
    {
        foreach (var node in nodes)
        {
            var seen = new HashSet<string>();
            foreach (var dependency in node.DependsOn)
            {
                if (dependency == node.Id) errors.Add($"node '{node.Id}' depends on itself");
                else if (!ids.Contains(dependency)) errors.Add($"node '{node.Id}' depends on unknown node '{dependency}'");

                if (!seen.Add(dependency)) errors.Add($"node '{node.Id}' lists dependency '{dependency}' more than once");
            }
        }
    }

    /// <summary>
    /// Depth-first topological sort. A cycle is reported with its full path, because "there is
    /// a cycle somewhere in your 40-node plan" is not an error message.
    /// </summary>
    private static List<PlanNode> sortByDependencies(List<PlanNode> nodes, List<string> errors)
    {
        var byId = nodes.ToDictionary(x => x.Id);
        var ordered = new List<PlanNode>();
        var done = new HashSet<string>();
        var inProgress = new List<string>();

        foreach (var node in nodes)
        {
            if (!visit(node) && errors.Count > 0) return ordered;
        }

        return ordered;

        bool visit(PlanNode node)
        {
            if (done.Contains(node.Id)) return true;

            var cycleStart = inProgress.IndexOf(node.Id);
            if (cycleStart >= 0)
            {
                var path = string.Join(" -> ", inProgress.Skip(cycleStart).Append(node.Id));
                errors.Add($"dependency cycle: {path}");
                return false;
            }

            inProgress.Add(node.Id);
            foreach (var dependency in node.DependsOn)
            {
                if (!visit(byId[dependency])) return false;
            }

            inProgress.RemoveAt(inProgress.Count - 1);
            done.Add(node.Id);
            ordered.Add(node);
            return true;
        }
    }

    // Unmatched keys throw by default (kept that way on purpose) and duplicate keys are opted
    // into being errors. snake_case is the wire convention everywhere in the monitor.
    private static readonly IDeserializer deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .Build();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SlugRegex();

    [GeneratedRegex("Property '([^']+)' not found on type")]
    private static partial Regex UnknownKeyRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+#[0-9]+$")]
    private static partial Regex IssueRefRegex();

    internal sealed class PlanDto
    {
        public int? Schema { get; set; }
        public string? Plan { get; set; }
        public string? Title { get; set; }
        public string? Anchor { get; set; }
        public Dictionary<string, string>? Repos { get; set; }
        public List<NodeDto>? Nodes { get; set; }
    }

    internal sealed class NodeDto
    {
        public string? Id { get; set; }
        public string? Kind { get; set; }
        public string? Title { get; set; }
        public string? Repo { get; set; }
        public int? Issue { get; set; }
        public int? Pr { get; set; }
        public string? Merge { get; set; }
        public string? Package { get; set; }
        public string? Feed { get; set; }
        public string? Bump { get; set; }
        public List<string>? DependsOn { get; set; }
    }
}
