using Bobcat.Monitor.Contracts;
using Bobcat.Monitor.Runs;
using Shouldly;

namespace Bobcat.Monitor.Tests;

/// <summary>
/// A supervised retry as it actually arrives on the wire — issue #84.
/// </summary>
/// <remarks>
/// The worker running the retry counts its attempts from one. Its tracking lives in
/// <c>MonitorPublishingObserver</c>, which belongs to a <c>BobcatRunner</c>, and the MTP host
/// builds a fresh runner for every run request — so a retry in a brand-new process and a retry
/// in a reused one both announce themselves as attempt 1. Only the supervisor knows better, and
/// it says so with <c>RetryScheduled</c>.
/// </remarks>
public class SupervisedRetryProjectionTests
{
    private static readonly Guid runId = Guid.NewGuid();
    private const string uid = "Orders/broker";

    private static RunProjection fold(params MonitorEvent[] events)
    {
        var projection = new RunProjection(runId);
        foreach (var @event in events) projection.Apply(@event);
        return projection;
    }

    private static MonitorEvent[] attempt(int workerReportedAs, string stepId, string status, string? error)
        =>
        [
            new ScenarioStarted(runId, uid, "Orders", "broker", workerReportedAs, DateTimeOffset.UtcNow),
            new StepStarted(runId, uid, stepId, "When", "the broker is asked"),
            new StepFinished(runId, uid, stepId, status, 500, error)
        ];

    [Fact]
    public void a_scheduled_retry_names_the_attempt_the_worker_could_not_know_it_was_on()
    {
        var run = fold([
            .. attempt(1, "s1", "error", "TimeoutException"),
            new RetryScheduled(runId, uid, 2, "RetryInFreshProcess", "the broker is slow to warm up"),
            // A fresh process: it has never seen this test before, so it says attempt 1.
            .. attempt(1, "s2", "success", null),
            new ScenarioFinished(runId, uid, "PassOnRetry", 1, 900, null)
        ]);

        var scenario = run.Scenarios.Single();

        scenario.Attempt.ShouldBe(2);

        // And the total is corrected too: the worker reported its own count of 1, which cannot
        // be fewer than the attempts we watched start.
        scenario.Attempts.ShouldBe(2);
    }

    [Fact]
    public void each_supervised_attempt_keeps_its_own_step_history()
    {
        // What CTRF's retryAttempts[] is rendered from. Before this, a supervised retry
        // overwrote its predecessor and the history was simply lost.
        var run = fold([
            .. attempt(1, "s1", "error", "first failure"),
            new RetryScheduled(runId, uid, 2, "RetryInFreshProcess", "flaky broker"),
            .. attempt(1, "s2", "error", "second failure"),
            new RetryScheduled(runId, uid, 3, "RetryAfterRecycle", "recycling rabbit"),
            .. attempt(1, "s3", "success", null),
            new ScenarioFinished(runId, uid, "PassOnRetry", 1, 900, null)
        ]);

        var scenario = run.Scenarios.Single();

        scenario.Attempt.ShouldBe(3);
        scenario.PriorAttempts.Select(a => a.Attempt).ShouldBe([1, 2]);

        // Each archived attempt carries the policy's verdict, which is the fact only the
        // supervisor had.
        scenario.PriorAttempts[0].Disposition.ShouldBe("RetryInFreshProcess");
        scenario.PriorAttempts[0].ErrorMessage.ShouldBe("first failure");
        scenario.PriorAttempts[1].Disposition.ShouldBe("RetryAfterRecycle");
        scenario.PriorAttempts[1].ErrorMessage.ShouldBe("second failure");

        // The live list is the final attempt only.
        scenario.Steps.Select(s => s.StepId).ShouldBe(["s3"]);
    }

    [Fact]
    public void an_in_process_retry_that_numbers_itself_correctly_is_left_alone()
    {
        // The correction is a floor, never an override: a front-end that knows its own attempt
        // number keeps it.
        var run = fold([
            .. attempt(1, "s1", "error", "boom"),
            new RetryScheduled(runId, uid, 2, "RetryInProcess", "flaky"),
            .. attempt(2, "s2", "success", null),
            new ScenarioFinished(runId, uid, "PassOnRetry", 2, 900, null)
        ]);

        var scenario = run.Scenarios.Single();
        scenario.Attempt.ShouldBe(2);
        scenario.Attempts.ShouldBe(2);
        scenario.PriorAttempts.Select(a => a.Attempt).ShouldBe([1]);
    }

    [Fact]
    public void the_pinned_number_belongs_to_the_scenario_that_was_retried()
    {
        var run = fold([
            .. attempt(1, "s1", "error", "boom"),
            new RetryScheduled(runId, uid, 2, "RetryInProcess", "flaky"),
            .. attempt(1, "s2", "success", null),
            new ScenarioStarted(runId, "Calc/adds", "Calc", "adds", 1, DateTimeOffset.UtcNow)
        ]);

        run.Scenarios.Single(s => s.Uid == "Calc/adds").Attempt.ShouldBe(1);
        run.Scenarios.Single(s => s.Uid == uid).Attempt.ShouldBe(2);
    }

    [Fact]
    public void an_attempt_number_never_goes_backwards()
    {
        // Hydration replays the archived stream over whatever live events already arrived, so
        // a start event for an attempt we have already watched happen arrives routinely. It
        // must not un-know the attempt.
        var run = fold([
            .. attempt(1, "s1", "error", "boom"),
            new RetryScheduled(runId, uid, 2, "RetryInProcess", "flaky"),
            .. attempt(1, "s2", "success", null),
            .. attempt(1, "s2", "success", null)
        ]);

        run.Scenarios.Single().Attempt.ShouldBe(2);
    }
}
