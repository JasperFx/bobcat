using Bobcat.Engine;
using Bobcat.Engine.Verification;

namespace Bobcat.Projected.Acceptance.Tests;

/// <summary>
/// <c>WaitFor.feature</c> from the projected lane. A projected helper polls by calling
/// <see cref="SpecAssert"/>'s <c>EventuallyAsync</c>.
/// </summary>
/// <remarks>
/// The assertions are the shape a reader gets: one step, one cell, a "converged after" or "timed
/// out after" note, and the last value seen.
/// </remarks>
public class WaitForTests
{
    public class PollingGrammar
    {
        private int _outstandingCalls;
        private int _checkCalls;
        private int _drainCalls;

        private static readonly TimeSpan every = TimeSpan.FromMilliseconds(10);

        /// <summary>Answers 5 on the first two polls, then 0.</summary>
        [Then("the outstanding count becomes {count}")]
        internal Task OutstandingBecomes(int count)
            => SpecAssert.EventuallyAsync("count", () => ++_outstandingCalls >= 3 ? 0 : 5, count,
                TimeSpan.FromSeconds(2), every);

        [Then("the never-ready count becomes {count}")]
        internal Task NeverReadyBecomes(int count)
            => SpecAssert.EventuallyAsync("count", () => 9, count, TimeSpan.FromMilliseconds(60), every);

        [Check("the system is eventually ready")]
        internal Task<bool> IsReady()
            => SpecAssert.EventuallyAsync(() => ++_checkCalls >= 2, TimeSpan.FromSeconds(2), every);

        [When("the queue eventually drains")]
        internal Task Drain() => SpecAssert.EventuallyAsync(() =>
        {
            if (++_drainCalls < 2) throw new InvalidOperationException("not drained yet");
        }, TimeSpan.FromSeconds(2), every);

        [Then("the average settles on {average}")]
        internal Task AverageSettlesOn(double average)
            => SpecAssert.EventuallyAsync("average", () => 2.46, average, TimeSpan.FromSeconds(2), every,
                new CheckOptions { Tolerance = 0.1 });
    }

    private readonly PollingGrammar _grammar = new();

    [Fact]
    public async Task a_value_check_converges_and_says_so()
    {
        var (recording, _) = await Projected.RunAsync("converges", () => _grammar.OutstandingBecomes(0));

        var step = recording.Step("the outstanding count becomes 0");
        step.Status.ShouldBe(ResultStatus.success);

        // One cell for the whole poll, not one per attempt.
        var cell = step.Cells.ShouldHaveSingleItem();
        cell.Actual.ShouldBe("0");
        cell.Note.ShouldNotBeNull().ShouldContain("converged after");
        cell.Note.ShouldContain("(3 polls)");
    }

    [Fact]
    public async Task a_timeout_fails_reporting_the_last_actual_value()
    {
        var (recording, _) = await Projected.RunAsync("times out", () => _grammar.NeverReadyBecomes(0));

        var step = recording.Step("the never-ready count becomes 0");
        step.Status.ShouldBe(ResultStatus.failed);

        var cell = step.Cells.ShouldHaveSingleItem();
        cell.Status.ShouldBe(ResultStatus.failed);
        cell.Expected.ShouldBe("0");
        cell.Actual.ShouldBe("9");
        cell.Note.ShouldNotBeNull().ShouldContain("timed out after 60ms @10ms");

        // A wrong, gathered rather than thrown: the scenario carries on and settles its verdict at the end.
        recording.GatheredFailures().ShouldNotBeNull().ShouldContain("the never-ready count becomes 0");
    }

    [Fact]
    public async Task a_fact_converges()
    {
        var (recording, _) = await Projected.RunAsync("fact converges", () => _grammar.IsReady());

        var step = recording.Step("the system is eventually ready");
        step.Status.ShouldBe(ResultStatus.success);
        step.Cells.ShouldHaveSingleItem().Note.ShouldNotBeNull().ShouldContain("converged after");
    }

    [Fact]
    public async Task an_action_is_retried_until_it_stops_throwing()
    {
        var (recording, _) = await Projected.RunAsync("action converges", () => _grammar.Drain());

        var step = recording.Step("the queue eventually drains");
        step.Status.ShouldBe(ResultStatus.success);
        step.Failure.ShouldBeNull();
    }

    [Fact]
    public async Task a_tolerance_rides_along_with_the_poll()
    {
        var (recording, _) = await Projected.RunAsync("approximately", () => _grammar.AverageSettlesOn(2.5));

        var cell = recording.Step("the average settles on 2.5").Cells.ShouldHaveSingleItem();
        cell.Status.ShouldBe(ResultStatus.success);
        cell.Note.ShouldNotBeNull().ShouldContain("±0.1");
        cell.Note.ShouldContain("converged after");
    }
}
