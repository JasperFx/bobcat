using System.Globalization;

namespace Bobcat.Engine.Verification;

/// <summary>
/// Resolves relative date/time tokens against a clock: <c>TODAY</c> (date-only),
/// <c>NOW</c> (date-time), and offsets — <c>TODAY+3</c> (days), <c>NOW + 5 minutes</c>,
/// <c>TODAY - 1 week</c> — using the friendly-duration parser. Used by the date/time
/// checkers so a spec can assert relative dates deterministically.
/// </summary>
public static class RelativeTimeResolver
{
    public static bool TryResolve(string text, TimeProvider clock, out DateTime resolved, out string note)
    {
        resolved = default;
        note = "";

        var t = text.Trim();
        var upper = t.ToUpperInvariant();

        var isToday = upper == "TODAY" || upper.StartsWith("TODAY+") || upper.StartsWith("TODAY-")
                      || upper.StartsWith("TODAY ");
        var isNow = upper == "NOW" || upper.StartsWith("NOW+") || upper.StartsWith("NOW-")
                    || upper.StartsWith("NOW ");
        if (!isToday && !isNow) return false;

        var now = clock.GetUtcNow().UtcDateTime;
        var token = isToday ? "TODAY" : "NOW";
        var basis = isToday ? now.Date : now;
        var rest = t.Substring(token.Length).Trim();

        var value = basis;
        if (rest.Length > 0)
        {
            var sign = rest[0];
            if (sign != '+' && sign != '-') return false;

            var operand = rest.Substring(1).Trim();
            if (!tryOffset(isToday, operand, out var offset)) return false;

            value = sign == '-' ? basis.Add(-offset) : basis.Add(offset);
        }

        resolved = value;

        // The token as written, and nothing more. The resolved value is what the cell itself shows —
        // a note repeating it read "2026-09-28 (TODAY-2 → 2026-09-28)", saying the same date twice.
        // What the note is FOR is telling the reader the document said TODAY-2 rather than a date.
        note = t;
        return true;
    }

    private static bool tryOffset(bool isToday, string operand, out TimeSpan offset)
    {
        // A bare integer after TODAY means days.
        if (isToday && int.TryParse(operand, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
        {
            offset = TimeSpan.FromDays(days);
            return true;
        }

        return FriendlyTimeSpanParser.TryParse(operand, out offset);
    }
}
