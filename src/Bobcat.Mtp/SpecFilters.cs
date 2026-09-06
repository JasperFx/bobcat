using Bobcat.Runtime;
using Microsoft.Testing.Platform.CommandLine;

namespace Bobcat.Mtp;

/// <summary>
/// The host's friendly filters (issue #207): <c>--filter-feature</c> and <c>--filter-tag</c>,
/// the same levers <c>ConsolePreview run --feature/--tag</c> offers in-process. Pure and static,
/// like <see cref="SpecNodeMapping"/>, so the semantics are testable without a host.
/// </summary>
/// <remarks>
/// The semantics deliberately mirror <c>BobcatRunner</c>'s own filtering, so a filter means the
/// same thing however a suite is driven: a feature filter is a case-insensitive substring of the
/// feature title; a tag filter matches a scenario tag exactly, case-insensitively. On the
/// command line the tag is written bare (<c>--filter-tag regression</c>) — the platform consumes
/// any <c>@</c>-prefixed argument as a response-file reference before option parsing sees it —
/// but a leading <c>@</c> from a programmatic caller is trimmed rather than silently matching
/// nothing. Several values of one kind are OR; the kinds are AND — and everything
/// still intersects with the platform's own uid filter, so a supervisor's selective re-run is
/// unaffected.
/// </remarks>
public static class SpecFilters
{
    /// <summary>The option name, without the <c>--</c> prefix, as MTP registers it.</summary>
    public const string FeatureOption = "filter-feature";

    /// <summary>The option name, without the <c>--</c> prefix, as MTP registers it.</summary>
    public const string TagOption = "filter-tag";

    /// <summary>Reads both filter option values off the parsed command line.</summary>
    public static (IReadOnlyList<string> Features, IReadOnlyList<string> Tags) From(
        ICommandLineOptions? options)
    {
        if (options is null) return ([], []);

        options.TryGetOptionArgumentList(FeatureOption, out var features);
        options.TryGetOptionArgumentList(TagOption, out var tags);

        return (features ?? [], tags ?? []);
    }

    public static bool Matches(FeatureDefinition feature, ScenarioDefinition scenario,
        IReadOnlyList<string> featureFilters, IReadOnlyList<string> tagFilters)
        => MatchesFeature(feature.Title, featureFilters) && MatchesTags(scenario.Tags, tagFilters);

    /// <summary>Case-insensitive substring on the feature title, ORed across values.</summary>
    public static bool MatchesFeature(string featureTitle, IReadOnlyList<string> featureFilters)
        => featureFilters.Count == 0
           || featureFilters.Any(f => featureTitle.Contains(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Case-insensitive exact match on any scenario tag, ORed across values. Feature-level tags
    /// are already merged into every scenario's tag list by the parser, so one rule covers both
    /// placements.
    /// </summary>
    public static bool MatchesTags(IEnumerable<string> tags, IReadOnlyList<string> tagFilters)
        => tagFilters.Count == 0
           || tags.Any(tag => tagFilters.Any(f =>
               tag.Equals(f.TrimStart('@'), StringComparison.OrdinalIgnoreCase)));
}
