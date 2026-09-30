using System.Globalization;
using System.Text.RegularExpressions;

namespace Bobcat.Engine.Verification;

/// <summary>
/// Parses human-friendly duration text into a <see cref="TimeSpan"/>.
/// Accepts the canonical form (<c>00:05:00</c>, <c>1.02:03:04</c>), worded forms
/// (<c>5 minutes</c>, <c>2 hours</c>, <c>1.5 seconds</c>, <c>1 week</c>), short suffixes
/// (<c>30s</c>), and compound compact forms (<c>1d2h30m</c>).
/// </summary>
public static class FriendlyTimeSpanParser
{
    private static readonly Regex tokenRegex = new(
        @"(?<value>\d+(\.\d+)?)\s*(?<unit>milliseconds?|millis?|ms|seconds?|secs?|minutes?|mins?|hours?|hrs?|days?|weeks?|wks?|h|d|s|m|w)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryParse(string? text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();

        // Canonical form (contains ':') — defer to the framework.
        if (s.Contains(':'))
        {
            return TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out result);
        }

        var matches = tokenRegex.Matches(s);
        if (matches.Count == 0) return false;

        // Everything outside the matched tokens must be pure separators ("and", commas, whitespace).
        var residue = tokenRegex.Replace(s, "");
        residue = Regex.Replace(residue, @"and|[\s,]", "", RegexOptions.IgnoreCase);
        if (residue.Length > 0) return false;

        var total = TimeSpan.Zero;
        foreach (Match m in matches)
        {
            var value = double.Parse(m.Groups["value"].Value, CultureInfo.InvariantCulture);
            var unit = m.Groups["unit"].Value.ToLowerInvariant();
            total += toTimeSpan(value, unit);
        }

        result = total;
        return true;
    }

    private static TimeSpan toTimeSpan(double value, string unit)
    {
        // Order matters: milliseconds must be recognized before the bare 'm' (minutes).
        if (unit == "ms" || unit.StartsWith("milli")) return TimeSpan.FromMilliseconds(value);

        return unit[0] switch
        {
            // A week is the largest unit with a fixed length. Months and years are not here on
            // purpose: their length depends on which month, so `TODAY + 1 month` would resolve to a
            // different offset in February than in March and a spec asserting it would drift.
            'w' => TimeSpan.FromDays(value * 7),
            'd' => TimeSpan.FromDays(value),
            'h' => TimeSpan.FromHours(value),
            's' => TimeSpan.FromSeconds(value),
            'm' => TimeSpan.FromMinutes(value),
            _ => TimeSpan.Zero
        };
    }
}
