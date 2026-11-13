using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>The degradation signal's rules (bobcat#824), as a pure function over finished tests.</summary>
public class DegradationDetectorTests
{
    private static readonly DateTimeOffset at = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    private static DegradationDetector detector(int window = 5, double factor = 10, TimeSpan? floor = null, params string[] knownUids)
        => new(window, factor, floor ?? TimeSpan.FromSeconds(1),
            knownUids.ToDictionary(uid => uid, _ => TimeSpan.FromSeconds(2), StringComparer.Ordinal));

    private static RunDegradation? observe(DegradationDetector detector, string uid, TimeSpan observed)
        => detector.Observe(uid, uid, observed, at).Degraded;

    [Fact]
    public void the_window_must_fill_with_slow_tests_and_then_clears()
    {
        var d = detector(window: 3, knownUids: ["a", "b", "c", "d", "e", "f"]);
        var slow = TimeSpan.FromSeconds(60);

        observe(d, "a", slow).ShouldBeNull();
        observe(d, "b", slow).ShouldBeNull();
        var degraded = observe(d, "c", slow).ShouldNotBeNull();

        degraded.Cause.ShouldBe(DegradationCause.SlowTests);
        degraded.SlowTests.Select(t => t.Uid).ShouldBe(["a", "b", "c"]);
        degraded.Pace.ShouldNotBeNull().Observed.ShouldBe(slow);
        degraded.Pace.Known.ShouldBe(TimeSpan.FromSeconds(2));
        degraded.Pace.Factor.ShouldBe(30);
        degraded.Reason.ShouldContain("the last 3 completed test(s) with a known duration each ran more than 10x it");

        // Cleared: two more slow tests are not yet a second signal, the third is.
        observe(d, "d", slow).ShouldBeNull();
        observe(d, "e", slow).ShouldBeNull();
        observe(d, "f", slow).ShouldNotBeNull();
    }

    [Fact]
    public void one_fast_test_breaks_the_run_of_slow_ones()
    {
        var d = detector(window: 3, knownUids: ["a", "b", "c", "d"]);
        var slow = TimeSpan.FromSeconds(60);

        observe(d, "a", slow).ShouldBeNull();
        observe(d, "b", slow).ShouldBeNull();
        observe(d, "c", TimeSpan.FromSeconds(2)).ShouldBeNull();
        // The window is now b, c, d — c was at pace.
        observe(d, "d", slow).ShouldBeNull();
    }

    [Fact]
    public void a_test_with_no_known_duration_does_not_count_so_a_first_run_never_degrades()
    {
        var d = detector(window: 2, knownUids: ["known"]);

        observe(d, "unknown-1", TimeSpan.FromMinutes(5)).ShouldBeNull();
        observe(d, "unknown-2", TimeSpan.FromMinutes(5)).ShouldBeNull();
        observe(d, "unknown-3", TimeSpan.FromMinutes(5)).ShouldBeNull();

        // Only the known one fills the window.
        observe(d, "known", TimeSpan.FromMinutes(5)).ShouldBeNull();
        observe(d, "known", TimeSpan.FromMinutes(5)).ShouldNotBeNull();

        var none = new DegradationDetector(2, 10, TimeSpan.FromSeconds(1), new Dictionary<string, TimeSpan>());
        observe(none, "x", TimeSpan.FromHours(1)).ShouldBeNull();
        observe(none, "y", TimeSpan.FromHours(1)).ShouldBeNull();
    }

    [Fact]
    public void a_test_under_the_floor_is_never_slow_whatever_its_known_duration()
    {
        // 5ms known, 60ms observed is 12x and means nothing: jitter on a fast suite must not
        // read as a degraded service.
        var d = new DegradationDetector(2, 10, TimeSpan.FromSeconds(1),
            new Dictionary<string, TimeSpan> { ["fast"] = TimeSpan.FromMilliseconds(5) });

        observe(d, "fast", TimeSpan.FromMilliseconds(60)).ShouldBeNull();
        observe(d, "fast", TimeSpan.FromMilliseconds(60)).ShouldBeNull();
        observe(d, "fast", TimeSpan.FromMilliseconds(999)).ShouldBeNull();

        // Past the floor and past the factor: slow.
        observe(d, "fast", TimeSpan.FromSeconds(1)).ShouldBeNull();
        observe(d, "fast", TimeSpan.FromSeconds(1)).ShouldNotBeNull();
    }

    [Fact]
    public void suspended_while_a_heal_is_pending_then_measures_the_pace_after_it()
    {
        var d = detector(window: 2, knownUids: ["a", "b", "c", "d"]);
        var slow = TimeSpan.FromSeconds(60);

        observe(d, "a", slow).ShouldBeNull();
        observe(d, "b", slow).ShouldNotBeNull();

        // Pending: tests finishing now ran against the degraded service and are not a second signal.
        d.Suspend();
        observe(d, "c", slow).ShouldBeNull();
        observe(d, "d", slow).ShouldBeNull();

        // Resumed: the next window is the after pace, and the signal is armed again.
        d.Resume();
        d.Observe("a", "a", TimeSpan.FromSeconds(2), at).AfterPace.ShouldBeNull();
        var after = d.Observe("b", "b", TimeSpan.FromSeconds(3), at).AfterPace.ShouldNotBeNull();
        after.Observed.ShouldBe(TimeSpan.FromSeconds(2.5));
        after.Samples.ShouldBe(2);
        d.PartialAfterPace().ShouldBeNull();

        observe(d, "c", slow).ShouldBeNull();
        observe(d, "d", slow).ShouldNotBeNull();
    }

    [Fact]
    public void a_partial_after_pace_is_whatever_finished_or_nothing()
    {
        var d = detector(window: 3, knownUids: ["a"]);

        d.Resume();
        d.PartialAfterPace().ShouldBeNull();

        d.Resume();
        d.Observe("a", "a", TimeSpan.FromSeconds(4), at);
        var partial = d.PartialAfterPace().ShouldNotBeNull();
        partial.Samples.ShouldBe(1);
        partial.Observed.ShouldBe(TimeSpan.FromSeconds(4));
        // Taken once.
        d.PartialAfterPace().ShouldBeNull();
    }

    [Fact]
    public void the_pace_of_a_stalled_test_is_its_time_in_flight_against_its_known_duration()
    {
        var d = detector(knownUids: ["a"]);
        d.PaceOf("a", TimeSpan.FromSeconds(31)).ShouldNotBeNull().Factor.ShouldBe(15.5);
        d.PaceOf("unknown", TimeSpan.FromSeconds(31)).ShouldBeNull();
    }
}
