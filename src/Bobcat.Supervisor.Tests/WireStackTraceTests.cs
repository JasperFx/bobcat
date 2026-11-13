using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#825 — the bound a worker's stack trace gets before it rides <c>test_finished</c>: the
/// first 40 lines within 4 KB, the throw site being at the top of a .NET trace, and a cut that is
/// never silent.
/// </summary>
public class WireStackTraceTests
{
    private static string frames(int count, string prefix = "   at Acme.Fixture.Frame")
        => string.Join("\n", Enumerable.Range(1, count).Select(i => $"{prefix}{i}() in Fixture.cs:line {i}"));

    [Fact]
    public void nothing_to_cap_is_null()
    {
        WireStackTrace.Cap(null).ShouldBeNull();
        WireStackTrace.Cap("").ShouldBeNull();
        WireStackTrace.Cap("   \n ").ShouldBeNull();
    }

    [Fact]
    public void a_trace_inside_both_bounds_is_returned_as_it_was()
    {
        var trace = frames(WireStackTrace.MaxLines);
        WireStackTrace.Cap(trace).ShouldBeSameAs(trace);

        // Windows line endings included — untouched means untouched.
        var windows = frames(3).Replace("\n", "\r\n");
        WireStackTrace.Cap(windows).ShouldBeSameAs(windows);
    }

    [Fact]
    public void too_many_lines_keeps_the_first_ones_and_ends_in_the_marker()
    {
        var capped = WireStackTrace.Cap(frames(200)).ShouldNotBeNull();
        var lines = capped.Split('\n');

        lines.Length.ShouldBe(WireStackTrace.MaxLines + 1);
        lines[0].ShouldBe("   at Acme.Fixture.Frame1() in Fixture.cs:line 1");
        lines[^2].ShouldBe($"   at Acme.Fixture.Frame{WireStackTrace.MaxLines}() in Fixture.cs:line {WireStackTrace.MaxLines}");
        lines[^1].ShouldBe(WireStackTrace.TruncationMarker);
    }

    [Fact]
    public void too_many_characters_cuts_on_a_line_boundary_inside_the_budget()
    {
        // Twenty lines of ~400 characters: under the line cap, well over the character one.
        var wide = frames(20, "   at " + new string('N', 380) + ".Frame");
        var capped = WireStackTrace.Cap(wide).ShouldNotBeNull();
        var lines = capped.Split('\n');

        lines[^1].ShouldBe(WireStackTrace.TruncationMarker);
        lines.Length.ShouldBeGreaterThan(2);
        // Every kept line is a whole frame, and the kept text fits the budget.
        var kept = lines[..^1];
        foreach (var line in kept) line.ShouldMatch(@"\.Frame\d+\(\) in Fixture\.cs:line \d+$");
        string.Join("\n", kept).Length.ShouldBeLessThanOrEqualTo(WireStackTrace.MaxChars);
    }

    [Fact]
    public void one_enormous_line_is_cut_hard_rather_than_kept_whole()
    {
        var giant = new string('x', WireStackTrace.MaxChars * 3);
        var capped = WireStackTrace.Cap(giant).ShouldNotBeNull();

        capped.ShouldStartWith(new string('x', WireStackTrace.MaxChars));
        capped.ShouldEndWith("\n" + WireStackTrace.TruncationMarker);
        capped.Length.ShouldBe(WireStackTrace.MaxChars + 1 + WireStackTrace.TruncationMarker.Length);
    }
}
