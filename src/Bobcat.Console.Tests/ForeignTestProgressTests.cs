using Bobcat.Console.Contracts;
using Bobcat.Console.Runs;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// Issue #195, the server-side fold: the supervisor's forwarded per-test stream is the only
/// per-test progress a run whose workers are not Bobcat runners produces, and it has to move the
/// same <c>ScenariosFinished</c> figure the dashboard reads without disturbing a Bobcat worker
/// that is publishing its own richer stream onto the same run.
/// </summary>
/// <remarks>
/// The precedence cases here are ported into the dashboard's <c>runs-store-foreign.test.ts</c>,
/// event for event, so the Pinia fold and this one cannot drift — the same discipline
/// <see cref="SupervisorTopologyProjectionTests"/> keeps.
/// </remarks>
public class ForeignTestProgressTests
{
    private static readonly Guid run = Guid.Parse("9a1f1a1e-0000-0000-0000-000000000195");

    private static DateTimeOffset at(string iso) => DateTimeOffset.Parse(iso);

    private static RunProjection fold(params MonitorEvent[] events)
    {
        var projection = new RunProjection(run);
        foreach (var @event in events) projection.Apply(@event);
        return projection;
    }

    private static int finished(RunProjection projection)
        => projection.Scenarios.Count(s => s.Outcome != null);

    [Fact]
    public void a_forwarded_verdict_makes_a_foreign_test_count_as_finished()
    {
        var projection = fold(
            new RunStarted(run, "ServiceTests", "/repo", "main", "supervised", at("2026-09-01T10:00:00Z"), 3),
            new TestStarted(run, "Acme.OrderTests.pays", "Acme.OrderTests.pays", 0, at("2026-09-01T10:00:01Z")),
            new TestFinished(run, "Acme.OrderTests.pays", "Acme.OrderTests.pays", "Passed", 120, 0,
                at("2026-09-01T10:00:03Z")));

        var scenario = projection.Scenarios.ShouldHaveSingleItem();
        scenario.Uid.ShouldBe("Acme.OrderTests.pays");
        scenario.Scenario.ShouldBe("Acme.OrderTests.pays");
        scenario.Outcome.ShouldBe("CleanPass");
        scenario.State.ShouldBe("Passed");
        scenario.DurationMs.ShouldBe(120);
        finished(projection).ShouldBe(1);
    }

    [Fact]
    public void an_in_progress_test_is_known_but_not_counted_as_finished()
    {
        var projection = fold(
            new TestStarted(run, "Acme.OrderTests.pays", "Acme.OrderTests.pays", 0, at("2026-09-01T10:00:01Z")));

        projection.Scenarios.ShouldHaveSingleItem().Outcome.ShouldBeNull();
        finished(projection).ShouldBe(0);
    }

    [Fact]
    public void a_foreign_test_never_invents_a_feature_for_itself()
    {
        // Spec identity is {Feature}/{Scenario}; a dotted xUnit method name is not one, and
        // guessing a feature out of it would put a name on the board nothing else agrees with.
        fold(new TestFinished(run, "Acme.OrderTests.pays", "Acme.OrderTests.pays", "Passed", 1, 0,
                at("2026-09-01T10:00:03Z")))
            .Scenarios.ShouldHaveSingleItem().Feature.ShouldBe("");
    }

    [Theory]
    [InlineData("Passed", "CleanPass")]
    [InlineData("Skipped", "CleanPass")]
    [InlineData("Failed", "Failed")]
    [InlineData("Error", "Failed")]
    [InlineData("Timeout", "Failed")]
    [InlineData("Cancelled", "Failed")]
    // A framework word this build has never met still finished; not counting it would stall the
    // progress bar for the whole run, which is the failure this issue exists to remove.
    [InlineData("SomethingNewInMtp", "Failed")]
    public void the_framework_word_maps_onto_the_run_outcome_vocabulary(string state, string outcome)
    {
        ForeignTestOutcome.From(state).ShouldBe(outcome);

        var scenario = fold(new TestFinished(run, "t", "t", state, null, 0, at("2026-09-01T10:00:03Z")))
            .Scenarios.ShouldHaveSingleItem();

        scenario.Outcome.ShouldBe(outcome);
        // The framework's own word survives beside the mapping — "skipped" is a fact the
        // RunOutcome vocabulary has no word for.
        scenario.State.ShouldBe(state);
    }

    [Fact]
    public void a_duration_the_supervisor_could_not_measure_is_null_not_zero()
        => fold(new TestFinished(run, "t", "t", "Passed", null, 0, at("2026-09-01T10:00:03Z")))
            .Scenarios.ShouldHaveSingleItem().DurationMs.ShouldBeNull();

    // A supervised Bobcat suite puts BOTH streams on the wire — the supervisor forwards every
    // worker's per-test updates, including workers that publish their own scenarios. The fold is
    // what keeps one, and it must do so in either arrival order.

    [Fact]
    public void a_worker_published_scenario_wins_when_the_forwarded_verdict_arrives_after_it()
    {
        var projection = fold(
            new ScenarioStarted(run, "Calc/adds", "Calc", "adds", 1, at("2026-09-01T10:00:01Z")),
            new ScenarioFinished(run, "Calc/adds", "PassOnRetry", 2, 500, "flaked once"),
            new TestFinished(run, "Calc/adds", "Calc/adds", "Passed", 480, 0, at("2026-09-01T10:00:03Z")));

        var scenario = projection.Scenarios.ShouldHaveSingleItem();
        scenario.Outcome.ShouldBe("PassOnRetry");
        scenario.Attempts.ShouldBe(2);
        scenario.DurationMs.ShouldBe(500);
        scenario.ErrorMessage.ShouldBe("flaked once");
        scenario.State.ShouldBeNull();
        finished(projection).ShouldBe(1);
    }

    [Fact]
    public void a_worker_published_scenario_wins_when_the_forwarded_verdict_arrives_before_it()
    {
        var projection = fold(
            new TestFinished(run, "Calc/adds", "Calc/adds", "Passed", 480, 0, at("2026-09-01T10:00:03Z")),
            new ScenarioStarted(run, "Calc/adds", "Calc", "adds", 1, at("2026-09-01T10:00:01Z")),
            new ScenarioFinished(run, "Calc/adds", "PassOnRetry", 2, 500, "flaked once"));

        var scenario = projection.Scenarios.ShouldHaveSingleItem();
        scenario.Feature.ShouldBe("Calc");
        scenario.Scenario.ShouldBe("adds");
        scenario.Outcome.ShouldBe("PassOnRetry");
        scenario.DurationMs.ShouldBe(500);
        // One test, one card — the two streams never double-count the same uid.
        finished(projection).ShouldBe(1);
    }

    [Fact]
    public void a_forwarded_start_stands_down_for_a_scenario_the_worker_already_owns()
    {
        // Otherwise a Bobcat worker's finished scenario would be un-finished by the supervisor's
        // own echo of the same test starting, and the run would never read as complete.
        var projection = fold(
            new ScenarioStarted(run, "Calc/adds", "Calc", "adds", 1, at("2026-09-01T10:00:01Z")),
            new ScenarioFinished(run, "Calc/adds", "CleanPass", 1, 20, null),
            new TestStarted(run, "Calc/adds", "Calc/adds", 0, at("2026-09-01T10:00:05Z")));

        finished(projection).ShouldBe(1);
    }

    [Fact]
    public void a_replayed_start_older_than_the_verdict_does_not_un_finish_a_foreign_test()
    {
        // Hydration replays the archive over live state; a start that predates the verdict we
        // already have is history, not a new attempt.
        var projection = fold(
            new TestFinished(run, "t", "t", "Passed", 10, 0, at("2026-09-01T10:00:03Z")),
            new TestStarted(run, "t", "t", 0, at("2026-09-01T10:00:01Z")));

        finished(projection).ShouldBe(1);
    }

    [Fact]
    public void a_genuinely_newer_start_reopens_a_foreign_test_for_its_retry()
    {
        var projection = fold(
            new TestFinished(run, "t", "t", "Failed", 10, 0, at("2026-09-01T10:00:03Z")),
            new RetryScheduled(run, "t", 2, "RetryInFreshProcess", "flaky broker"),
            new TestStarted(run, "t", "t", null, at("2026-09-01T10:00:09Z")));

        projection.Scenarios.ShouldHaveSingleItem().Outcome.ShouldBeNull();
        finished(projection).ShouldBe(0);
    }
}
