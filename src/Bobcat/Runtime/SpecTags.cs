namespace Bobcat.Runtime;

/// <summary>
/// Well-known spec tags that affect execution behavior.
/// </summary>
public static class SpecTags
{
    /// <summary>Failure is informational — does not break CI.</summary>
    public const string Acceptance = "acceptance";

    /// <summary>Failure breaks CI. This is the default if no lifecycle tag is present.</summary>
    public const string Regression = "regression";

    /// <summary>
    /// Parse a @timeout(N) tag, returning the timeout in seconds, or null.
    /// </summary>
    public static int? ParseTimeout(string tag)
    {
        if (tag.StartsWith("timeout(") && tag.EndsWith(")") &&
            int.TryParse(tag.AsSpan(8, tag.Length - 9), out var seconds))
        {
            return seconds;
        }
        return null;
    }

    /// <summary>
    /// Parse a @retry(N) tag, returning the retry count, or null.
    /// </summary>
    public static int? ParseRetry(string tag)
    {
        if (tag.StartsWith("retry(") && tag.EndsWith(")") &&
            int.TryParse(tag.AsSpan(6, tag.Length - 7), out var count))
        {
            return count;
        }
        return null;
    }

    public static bool IsAcceptance(IEnumerable<string> tags)
        => tags.Any(t => t.Equals(Acceptance, StringComparison.OrdinalIgnoreCase));

    public static bool IsRegression(IEnumerable<string> tags)
        => !IsAcceptance(tags);

    public static TimeSpan? GetTimeout(IEnumerable<string> tags)
    {
        foreach (var tag in tags)
        {
            var seconds = ParseTimeout(tag);
            if (seconds.HasValue) return TimeSpan.FromSeconds(seconds.Value);
        }
        return null;
    }

    public static int GetRetryCount(IEnumerable<string> tags)
    {
        foreach (var tag in tags)
        {
            var count = ParseRetry(tag);
            if (count.HasValue) return count.Value;
        }
        return 0;
    }
}
