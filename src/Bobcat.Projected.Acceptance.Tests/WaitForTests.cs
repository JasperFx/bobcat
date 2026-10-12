using Bobcat.Engine;

namespace Bobcat.Projected.Acceptance.Tests;

/// <summary>
/// <c>WaitFor.feature</c> from the projected lane, and the gap it found: nothing in the projected
/// lane polls. <c>[WaitFor]</c> is read only by the Gherkin generator, whose emitted code hands the
/// step to <c>WaitForRunner.Poll</c>, and that takes a Gherkin <c>StepResult</c>. A projected helper
/// carrying the attribute compiles, runs once, and reports its first answer.
/// </summary>
/// <remarks>
/// The tests are written against the shape the Gherkin lane gives a reader (one step, a
/// "converged after" or "timed out after" note, the last value seen) so whichever form a projected
/// poll takes, they say what it has to report. Each one is skipped until it exists.
/// </remarks>
public class WaitForTests
{
    private const string gap =
        "Projected-lane gap (bobcat#471 phase 2): [WaitFor] is honoured only by the Gherkin generator, and " +
        "WaitForRunner.Poll takes a Gherkin StepResult, so a projected step never polls. Needs a projected " +
        "entry point (interceptor support for [WaitFor], or a SpecAssert-style poll helper) and a maintainer " +
        "decision on which.";

    public class PollingGrammar
    {
        private int _outstandingCalls;
        private int _checkCalls;
        private int _drainCalls;

        /// <summary>Answers 5 on the first two polls, then 0.</summary>
        [WaitFor(2000, PollAt = 10)]
        [Then("the outstanding count becomes {count}")]
        internal void OutstandingBecomes(int count)
            => SpecAssert.Check("count", ++_outstandingCalls >= 3 ? 0 : 5, count);

        [WaitFor(60, PollAt = 10)]
        [Then("the never-ready count becomes {count}")]
        internal void NeverReadyBecomes(int count) => SpecAssert.Check("count", 9, count);

        [WaitFor(2000, PollAt = 10)]
        [Check("the system is eventually ready")]
        internal bool IsReady() => ++_checkCalls >= 2;

        [WaitFor(2000, PollAt = 10)]
        [When("the queue eventually drains")]
        internal void Drain()
        {
            if (++_drainCalls < 2) throw new InvalidOperationException("not drained yet");
        }
    }

    private readonly PollingGrammar _grammar = new();

    [Fact(Skip = gap)]
    public void a_value_check_converges_and_says_so()
    {
        var (recording, _) = Projected.Run("converges", () => _grammar.OutstandingBecomes(0));

        var step = recording.Step("the outstanding count becomes 0");
        step.Status.ShouldBe(ResultStatus.success);
        step.Cells.Single().Note.ShouldNotBeNull().ShouldContain("converged after");
    }

    [Fact(Skip = gap)]
    public void a_timeout_fails_reporting_the_last_actual_value()
    {
        var (recording, _) = Projected.Run("times out", () => _grammar.NeverReadyBecomes(0));

        var cell = recording.Step("the never-ready count becomes 0").Cells.Single();
        cell.Status.ShouldBe(ResultStatus.failed);
        cell.Expected.ShouldBe("0");
        cell.Actual.ShouldBe("9");
        cell.Note.ShouldNotBeNull().ShouldContain("timed out after 60ms @10ms");
    }

    [Fact(Skip = gap)]
    public void a_fact_converges()
    {
        var (recording, _) = Projected.Run("fact converges", () => _grammar.IsReady());

        recording.Step("the system is eventually ready").Status.ShouldBe(ResultStatus.success);
    }

    [Fact(Skip = gap)]
    public void an_action_is_retried_until_it_stops_throwing()
    {
        var (recording, _) = Projected.Run("action converges", () => _grammar.Drain());

        recording.Step("the queue eventually drains").Status.ShouldBe(ResultStatus.success);
    }
}
