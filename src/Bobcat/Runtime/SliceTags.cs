namespace Bobcat.Runtime;

/// <summary>
/// The feature-level vocabulary that declares an Event Modeling slice — the only bits a
/// spec cannot derive from its own steps. Parsed from a feature's tags and description by the
/// generator and exposed on <see cref="FeatureDefinition"/>; nothing in the runtime acts on them.
/// They are evidence for tooling (the slice map, the viewer, #106), not execution rules.
/// </summary>
/// <remarks>
/// <code>
/// @slice:WithdrawFunds @domain:BankAccount
/// Feature: Withdraw Funds
///   Triggered by the account holder
/// </code>
/// Tags written on the <c>Feature:</c> line are inherited by every scenario in the feature
/// (standard Gherkin semantics), so a slice tag also reaches the scenario's traits —
/// <c>ResilienceTags.ToTraits</c> projects <c>key:value</c> tags to <c>key = value</c>.
/// </remarks>
public static class SliceTags
{
    /// <summary>Tag prefix naming the slice a feature specifies: <c>@slice:WithdrawFunds</c>.</summary>
    public const string SlicePrefix = "slice:";

    /// <summary>Tag prefix naming the domain/bounded context: <c>@domain:BankAccount</c>.</summary>
    public const string DomainPrefix = "domain:";

    /// <summary>Description-line prefix naming the trigger: <c>Triggered by the account holder</c>.</summary>
    public const string TriggeredByPrefix = "Triggered by";

    /// <summary>The slice name from a <c>slice:&lt;name&gt;</c> tag, or null.</summary>
    public static string? Slice(IEnumerable<string> tags) => valueOf(tags, SlicePrefix);

    /// <summary>The domain name from a <c>domain:&lt;name&gt;</c> tag, or null.</summary>
    public static string? Domain(IEnumerable<string> tags) => valueOf(tags, DomainPrefix);

    /// <summary>
    /// The trigger from a feature description line starting <c>Triggered by</c> — the remainder of
    /// that line, trimmed — or null when no such line exists.
    /// </summary>
    public static string? TriggeredBy(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;

        foreach (var raw in description.Split('\n'))
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
