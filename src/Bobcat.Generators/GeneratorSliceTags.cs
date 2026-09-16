using System;
using System.Collections.Generic;

namespace Bobcat.Generators;

/// <summary>
/// The generator's copy of <c>Bobcat.Runtime.SliceTags</c> — the feature-level vocabulary that
/// declares an Event Modeling slice (<c>@slice:</c>, <c>@domain:</c>, <c>@chapter:</c>,
/// <c>@pattern:</c>, <c>Triggered by …</c>).
/// </summary>
/// <remarks>
/// <para>
/// Duplicated, not shared, because this project is netstandard2.0 and references neither Bobcat
/// core nor anything else — it only ever recognizes things by name. That is the same constraint
/// that keeps it from referencing Marten or EF for the persistence recipes.
/// </para>
/// <para>
/// A silent divergence here would be expensive: the runtime would report one slice name on
/// <c>FeatureDefinition.Slice</c> and the generated descriptor another, so run evidence would
/// fail to join to the design-time model with nothing anywhere reporting an error.
/// <c>SliceTagParsingAgreementTests</c> pins the two implementations together, exactly as
/// <c>ResourceParsingAgreementTests</c> does for the recovery-hint resource lists that JasperFx
/// and Bobcat each parse.
/// </para>
/// <para>
/// Note tags arrive here with the leading <c>@</c> already stripped by
/// <c>SimpleGherkinParser</c>, so the stored tag is <c>slice:WithdrawFunds</c>.
/// </para>
/// </remarks>
internal static class GeneratorSliceTags
{
    public const string SlicePrefix = "slice:";
    public const string DomainPrefix = "domain:";
    public const string ChapterPrefix = "chapter:";
    /// <summary>
    /// The slice's Event Modeling pattern, when the model states it (issue #323). Gherkin cannot
    /// derive Automation or Translation from step text — a bus command and an automation's trigger
    /// event are the same sentence, `When X is received` — so without this tag the spec source
    /// guessed Command for every automation and disagreed with the curated model that knew better,
    /// permanently and unresolvably. A tag can say what a sentence cannot.
    /// </summary>
    public const string PatternPrefix = "pattern:";

    public const string TriggeredByPrefix = "Triggered by";

    public static string? Slice(IEnumerable<string> tags) => valueOf(tags, SlicePrefix);

    public static string? Domain(IEnumerable<string> tags) => valueOf(tags, DomainPrefix);

    public static string? Chapter(IEnumerable<string> tags) => valueOf(tags, ChapterPrefix);

    public static string? Pattern(IEnumerable<string> tags) => valueOf(tags, PatternPrefix);

    public static string? TriggeredBy(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;

        foreach (var raw in description!.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(TriggeredByPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = line.Substring(TriggeredByPrefix.Length).Trim().TrimStart(':').Trim();
            return rest.Length > 0 ? rest : null;
        }

        return null;
    }

    private static string? valueOf(IEnumerable<string> tags, string prefix)
    {
        foreach (var tag in tags)
        {
            if (!tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var value = tag.Substring(prefix.Length).Trim();
            if (value.Length > 0) return value;
        }

        return null;
    }
}
