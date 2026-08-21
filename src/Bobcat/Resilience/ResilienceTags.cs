namespace Bobcat.Resilience;

/// <summary>
/// The retry vocabulary, and the projection from Gherkin tags onto the trait dictionary a
/// <see cref="IFailurePolicy"/> reads.
/// </summary>
/// <remarks>
/// These names are engine-level, not spec-level. A Gherkin scenario writes <c>@isolated</c>; an
/// xUnit test writes <c>[Trait("Isolated","true")]</c>; both arrive at the policy as the same
/// trait. That is what lets one policy serve both front-ends.
/// </remarks>
public static class ResilienceTags
{
    /// <summary>Number of attempts allowed. Gherkin: <c>@retry(2)</c>. Trait: <c>Retry=2</c>.</summary>
    public const string Retry = "Retry";

    /// <summary>Only reliable when run alone. Gherkin: <c>@isolated</c>. Trait: <c>Isolated=true</c>.</summary>
    public const string Isolated = "Isolated";

    /// <summary>
    /// Resources to recycle before retrying, comma-separated. Gherkin: <c>@recycle(rabbit)</c>.
    /// Trait: <c>RecycleOnRetry=rabbit</c>.
    /// </summary>
    public const string RecycleOnRetry = "RecycleOnRetry";

    /// <summary>
    /// Projects Gherkin tags onto traits. Unrecognized tags come through as
    /// <c>tag =&gt; "true"</c> so a custom policy can key off any tag the author writes.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ToTraits(IEnumerable<string> tags)
    {
        var traits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in tags)
        {
            if (tryParseArgument(tag, "retry", out var retry))
            {
                traits[Retry] = retry;
            }
            else if (tryParseArgument(tag, "recycle", out var resources))
            {
                traits[RecycleOnRetry] = resources;
            }
            else if (tag.Equals("isolated", StringComparison.OrdinalIgnoreCase))
            {
                traits[Isolated] = "true";
            }
            else if (tryParseKeyValue(tag, out var key, out var value))
            {
                // @slice:WithdrawFunds → Slice = WithdrawFunds; @domain:BankAccount → Domain = ...
                // The generic form so a supervisor or the viewer can read any key:value tag an
                // author writes without Bobcat having to know the key.
                traits[key] = value;
            }
            else
            {
                traits[tag] = "true";
            }
        }

        return traits;
    }

    /// <summary>Parses <c>name(argument)</c>, e.g. <c>recycle(rabbit,kafka)</c>.</summary>
    private static bool tryParseArgument(string tag, string name, out string argument)
    {
        argument = "";

        if (tag.Length <= name.Length + 2) return false;
        if (!tag.StartsWith(name + "(", StringComparison.OrdinalIgnoreCase)) return false;
        if (!tag.EndsWith(")", StringComparison.Ordinal)) return false;

        argument = tag.Substring(name.Length + 1, tag.Length - name.Length - 2).Trim();
        return argument.Length > 0;
    }

    /// <summary>Parses <c>key:value</c>, e.g. <c>slice:WithdrawFunds</c>. Both halves must be non-empty.</summary>
    private static bool tryParseKeyValue(string tag, out string key, out string value)
    {
        key = "";
        value = "";

        var colon = tag.IndexOf(':');
        if (colon <= 0 || colon == tag.Length - 1) return false;

        key = tag.Substring(0, colon).Trim();
        value = tag.Substring(colon + 1).Trim();
        return key.Length > 0 && value.Length > 0;
    }

    /// <summary>Resource names from a <c>RecycleOnRetry</c> trait value.</summary>
    public static string[] ParseResources(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
