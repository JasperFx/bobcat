namespace Bobcat.Supervisor;

/// <summary>
/// Bounds a worker's stack trace before it rides <c>test_finished</c> (bobcat#825). One fixture
/// failing to start fails every test in its class with the same hundred-frame trace, and a
/// supervised suite can have a hundred such tests: unbounded, the reason would cost more of the
/// run's ndjson than every other event combined. The cap keeps the <b>first</b>
/// <see cref="MaxLines"/> lines, within <see cref="MaxChars"/>, because a .NET trace puts the
/// throw site at the top and the test framework's invoker frames at the bottom — the half a
/// reader diagnoses from is the half that survives.
/// </summary>
/// <remarks>
/// A cut is never silent: a capped trace ends in <see cref="TruncationMarker"/> on a line of its
/// own, so a reader knows there was more and where to get it (the worker's own output, or
/// <c>bobcat run</c>'s results file). A trace inside both bounds is returned as it was.
/// </remarks>
public static class WireStackTrace
{
    /// <summary>The most lines that go on the wire, marker excluded.</summary>
    public const int MaxLines = 40;

    /// <summary>The most characters that go on the wire, marker excluded — about 4 KB.</summary>
    public const int MaxChars = 4096;

    /// <summary>The last line of a capped trace.</summary>
    public const string TruncationMarker = "… (truncated)";

    public static string? Cap(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return null;

        var lines = stackTrace.Replace("\r\n", "\n").Split('\n');
        var cut = false;

        if (lines.Length > MaxLines)
        {
            lines = lines[..MaxLines];
            cut = true;
        }

        var kept = string.Join('\n', lines);
        if (kept.Length > MaxChars)
        {
            // Cut on a line boundary when one falls inside the budget, so the last line kept is
            // a whole frame rather than half of one.
            var boundary = kept.LastIndexOf('\n', MaxChars - 1);
            kept = kept[..(boundary > 0 ? boundary : MaxChars)];
            cut = true;
        }

        if (!cut) return stackTrace;

        return kept.TrimEnd('\n', '\r') + "\n" + TruncationMarker;
    }
}
