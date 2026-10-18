using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#704: a green run's stalls showed only as a log line, because the Stalled section sat
/// below the fold of <see cref="RunReport.ToText"/>. It now leads, beside the retries, and says
/// where each stall ran, how long, and how the test ended.
/// </summary>
public class StalledReportTests
{
    private static TestReport test(string uid, params SupervisorAttempt[] attempts)
        => new() { Uid = uid, DisplayName = $"Suite.{uid}", Attempts = attempts };

    private static SupervisorAttempt attempt(
        int number, string uid, WorkerTestState state, double? seconds = null, bool stallInduced = false)
        => new(number,
            new WorkerOutcome(uid, $"Suite.{uid}", state)
            {
                Duration = seconds is { } s ? TimeSpan.FromSeconds(s) : null
            },
            AttemptPlacement.Batched, Disposition.Pass) { StallInduced = stallInduced };

    private static StalledTest stalled(string uid, double seconds, int lane = 2,
        WorkerPurpose purpose = WorkerPurpose.Lane)
        => new(uid, $"Suite.{uid}", TimeSpan.FromSeconds(seconds), new WorkerLaunchContext(lane, purpose));

    private static string[] lines(string text) => text.Split(Environment.NewLine);

    [Fact]
    public void a_green_runs_stalls_lead_the_report_ahead_of_the_retries_and_the_quarantine()
    {
        var results = new SupervisorResults
        {
            Tests =
            [
                test("slow", attempt(1, "slow", WorkerTestState.Passed, 312)),
                test("flaky",
                    attempt(1, "flaky", WorkerTestState.Failed, 1),
                    attempt(2, "flaky", WorkerTestState.Passed, 1))
            ],
            StalledTests = [stalled("slow", 300)]
        };

        results.ExitCode.ShouldBe(0);

        var text = RunReport.ToText(results);
        var stalls = text.IndexOf("Stalled (in flight past the stall threshold):", StringComparison.Ordinal);

        stalls.ShouldBeGreaterThan(0);
        stalls.ShouldBeLessThan(text.IndexOf("Passed on retry", StringComparison.Ordinal));
        stalls.ShouldBeLessThan(text.IndexOf("Quarantine candidates", StringComparison.Ordinal));
    }

    [Fact]
    public void each_stall_names_its_test_lane_time_in_flight_and_how_it_finished()
    {
        var results = new SupervisorResults
        {
            Tests = [test("slow", attempt(1, "slow", WorkerTestState.Passed, 312))],
            StalledTests = [stalled("slow", 300, lane: 3)]
        };

        lines(RunReport.ToText(results)).ShouldContain(
            "  • Suite.slow — lane 3, 300s in flight when it stalled; finished Passed after 5m 12s");
    }

    [Fact]
    public void a_stall_that_never_finished_says_so()
    {
        var results = new SupervisorResults
        {
            Tests = [test("wedged", attempt(1, "wedged", WorkerTestState.Indeterminate))],
            StalledTests = [stalled("wedged", 45, lane: 1)]
        };

        lines(RunReport.ToText(results)).ShouldContain(
            "  • Suite.wedged — lane 1, 45s in flight when it stalled; never finished");
    }

    [Fact]
    public void a_stall_killed_and_retried_reports_the_attempt_that_finished()
    {
        var results = new SupervisorResults
        {
            Tests =
            [
                test("hangs",
                    attempt(1, "hangs", WorkerTestState.Indeterminate, stallInduced: true),
                    attempt(2, "hangs", WorkerTestState.Passed, 2))
            ],
            StalledTests = [stalled("hangs", 30, lane: 0)]
        };

        lines(RunReport.ToText(results)).ShouldContain(
            "  • Suite.hangs — lane 0, 30s in flight when it stalled; finished Passed after 2.0s on attempt 2");
    }

    [Fact]
    public void a_stall_in_a_solo_process_names_the_process_rather_than_a_lane()
    {
        var results = new SupervisorResults
        {
            Tests = [],
            StalledTests = [stalled("alone", 60, lane: 0, purpose: WorkerPurpose.Isolated)]
        };

        lines(RunReport.ToText(results)).ShouldContain(
            "  • Suite.alone — an isolated process, 60s in flight when it stalled; never finished (no result recorded)");
    }

    [Fact]
    public void no_stalls_means_no_section()
    {
        var results = new SupervisorResults
        {
            Tests = [test("fine", attempt(1, "fine", WorkerTestState.Passed, 1))]
        };

        RunReport.ToText(results).ShouldNotContain("Stalled");
    }
}
