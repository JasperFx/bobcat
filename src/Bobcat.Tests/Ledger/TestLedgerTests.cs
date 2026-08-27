using Bobcat.Engine;
using Bobcat.Ledger;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Ledger;

/// <summary>
/// The committed ledger (issues #44 layer 2 / #142 layers 2–3 / the WorkPlan feed) — and above
/// all its merge strategy, which #142 called the decision that determines whether people keep
/// the file or delete it in annoyance. The properties under test here ARE the design:
/// determinism (same observations → identical bytes, whoever folds), commutativity and
/// idempotence (any fold order, any repeat, one result), and a prune that never consults a
/// clock. Design of record: docs/ledger-design.md.
/// </summary>
public class TestLedgerTests
{
    private static readonly DateTimeOffset t0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static LedgerRun run(string runId, int day, string uid = "F/spec", long? totalMs = 100,
        string outcome = "CleanPass", int attempts = 1, string? failure = null, string? clearedBy = null,
        bool stallInduced = false)
        => new(runId, t0.AddDays(day), uid, uid, outcome, attempts)
        {
            TotalMs = totalMs,
            FirstMs = totalMs,
            Failure = failure,
            ClearedBy = clearedBy,
            StallInduced = stallInduced
        };

    // ---- the merge strategy -------------------------------------------------------------------

    [Fact]
    public void the_same_observations_serialize_to_identical_bytes_whatever_the_fold_order()
    {
        var observations = new[]
        {
            run("r1", 1, "B/two"), run("r2", 2, "A/one"), run("r3", 3, "B/two"), run("r4", 4, "A/one")
        };

        var forward = TestLedger.Empty().Record(observations).ToJson();
        var reversed = TestLedger.Empty().Record(observations.Reverse()).ToJson();
        var oneAtATime = observations.Reverse()
            .Aggregate(TestLedger.Empty(), (ledger, r) => ledger.Record([r])).ToJson();

        reversed.ShouldBe(forward);
        oneAtATime.ShouldBe(forward);
    }

    [Fact]
    public void merge_is_commutative_and_idempotent_so_a_git_conflict_always_resolves()
    {
        // Two CI machines fold different runs into their own copies of the committed file.
        var shared = TestLedger.Empty().Record([run("r1", 1)]);
        var machineA = shared.Record([run("r2", 2), run("r3", 3, "A/other")]);
        var machineB = shared.Record([run("r4", 4)]);

        var ab = machineA.Merge(machineB).ToJson();
        var ba = machineB.Merge(machineA).ToJson();

        // Either side can resolve the conflict, repeatedly, and converge on one file — no run
        // artifacts needed, no judgement calls.
        ba.ShouldBe(ab);
        machineA.Merge(machineB).Merge(machineB).ToJson().ShouldBe(ab);
        TestLedger.FromJson(ab).Merge(TestLedger.FromJson(ab)).ToJson().ShouldBe(ab);
    }

    [Fact]
    public void re_recording_the_same_run_changes_nothing()
    {
        var once = TestLedger.Empty().Record([run("r1", 1)]);
        once.Record([run("r1", 1)]).ToJson().ShouldBe(once.ToJson());
    }

    [Fact]
    public void aging_keeps_the_newest_runs_per_test_and_never_asks_the_time()
    {
        var ledger = TestLedger.Empty(maxRunsPerTest: 3)
            .Record(Enumerable.Range(1, 5).Select(day => run($"r{day}", day, totalMs: day * 100)));

        var runs = ledger.Tests["F/spec"];
        runs.Count.ShouldBe(3);
        runs.Select(r => r.RunId).ShouldBe(["r5", "r4", "r3"]);
    }

    [Fact]
    public void merging_ledgers_with_different_retention_keeps_the_larger()
    {
        var small = TestLedger.Empty(maxRunsPerTest: 2).Record([run("r1", 1)]);
        var large = TestLedger.Empty(maxRunsPerTest: 10).Record([run("r2", 2)]);

        small.Merge(large).MaxRunsPerTest.ShouldBe(10);
        large.Merge(small).MaxRunsPerTest.ShouldBe(10);
    }

    [Fact]
    public void deleted_tests_are_pruned_only_on_explicit_request_with_the_callers_clock()
    {
        var ledger = TestLedger.Empty().Record([run("r1", 1, "Old/gone"), run("r2", 10, "New/kept")]);

        // Nothing ages away on its own — the fold never reads a clock.
        ledger.Tests.Keys.ShouldBe(["New/kept", "Old/gone"]);

        var pruned = ledger.PruneTestsNotSeenSince(t0.AddDays(5));
        pruned.Tests.Keys.ShouldBe(["New/kept"]);
    }

    [Fact]
    public void the_file_round_trips_through_disk_and_a_missing_file_is_an_empty_ledger()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bobcat-ledger-{Guid.NewGuid():N}", "ledger.json");
        try
        {
            TestLedger.Load(path).Tests.ShouldBeEmpty();

            var ledger = TestLedger.Empty().Record([run("r1", 1)]);
            ledger.Save(path);

            TestLedger.Load(path).ToJson().ShouldBe(ledger.ToJson());
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }

    // ---- the three consumers ------------------------------------------------------------------

    [Fact]
    public void known_durations_are_medians_and_unmeasured_is_absent_not_zero()
    {
        var ledger = TestLedger.Empty().Record(
        [
            run("r1", 1, totalMs: 100), run("r2", 2, totalMs: 300), run("r3", 3, totalMs: 200),
            // A framework that erases durations (tUnit) contributes nothing, never zeroes.
            run("r4", 1, "F/unmeasured", totalMs: null)
        ]);

        var durations = ledger.KnownDurations();
        durations["F/spec"].ShouldBe(TimeSpan.FromMilliseconds(200));
        durations.ContainsKey("F/unmeasured").ShouldBeFalse();
    }

    [Fact]
    public void a_test_that_quietly_grew_shows_as_a_trend_no_single_run_can_see()
    {
        var ledger = TestLedger.Empty().Record(
        [
            run("r1", 1, totalMs: 2_000), run("r2", 2, totalMs: 2_100), run("r3", 3, totalMs: 1_900),
            run("r4", 4, totalMs: 39_000), run("r5", 5, totalMs: 41_000), run("r6", 6, totalMs: 40_000),
            // A steady test earns silence.
            run("s1", 1, "F/steady", 500), run("s2", 2, "F/steady", 510), run("s3", 3, "F/steady", 490),
            run("s4", 4, "F/steady", 505), run("s5", 5, "F/steady", 495), run("s6", 6, "F/steady", 500)
        ]);

        var trend = ledger.Trends().ShouldHaveSingleItem();
        trend.Uid.ShouldBe("F/spec");
        trend.Then.ShouldBe(TimeSpan.FromMilliseconds(2_000));
        trend.Now.ShouldBe(TimeSpan.FromMilliseconds(40_000));
        trend.GrowthFactor.ShouldBe(20);
    }

    [Fact]
    public void the_ledger_proposes_a_hint_and_the_suggestion_is_for_a_human_to_accept()
    {
        var ledger = TestLedger.Empty().Record(
        [
            run("r1", 1, outcome: "PassOnRetry", attempts: 2,
                failure: "System.TimeoutException", clearedBy: "RetryInProcess"),
            run("r2", 2, outcome: "PassOnRetry", attempts: 2,
                failure: "System.TimeoutException", clearedBy: "RetryInProcess"),
            run("r3", 3, "G/other", outcome: "PassOnRetry", attempts: 3,
                failure: "System.TimeoutException", clearedBy: "RetryInProcess"),
            // One lucky retry of something else is an anecdote, not evidence.
            run("r4", 4, "G/other", outcome: "PassOnRetry", attempts: 2,
                failure: "Broker.Unavailable", clearedBy: "RetryInProcess")
        ]);

        var proposal = ledger.ProposeHints(minOccurrences: 3).ShouldHaveSingleItem();
        proposal.FailureTypeName.ShouldBe("System.TimeoutException");
        proposal.ClearedBy.ShouldBe("RetryInProcess");
        proposal.Cleared.ShouldBe(3);
        proposal.Tests.ShouldBe(["F/spec", "G/other"]);
        // The output is attribute text a person copies into the code — the #44 fork: the
        // ledger proposes, a human accepts. Nothing wires this into a policy.
        proposal.Suggestion.ShouldContain("[ClearsOnRetry(typeof(TimeoutException)");
        proposal.Suggestion.ShouldContain("Because =");
    }

    [Fact]
    public void a_failure_that_never_recovers_earns_the_counterweight_proposal()
    {
        var ledger = TestLedger.Empty().Record(
        [
            run("r1", 1, outcome: "Failed", attempts: 2, failure: "My.DeterministicBug"),
            run("r2", 2, outcome: "Failed", attempts: 2, failure: "My.DeterministicBug"),
            run("r3", 3, outcome: "Failed", attempts: 2, failure: "My.DeterministicBug")
        ]);

        var proposal = ledger.ProposeHints(minOccurrences: 3).ShouldHaveSingleItem();
        proposal.Unrecovered.ShouldBe(3);
        proposal.Suggestion.ShouldContain("[NeverRecovers(typeof(DeterministicBug)");
    }

    [Fact]
    public void stall_induced_entries_never_feed_the_hint_evidence()
    {
        // A wedge is not a flake (issue #173) — killed-worker entries carry no information
        // about the failure class.
        var ledger = TestLedger.Empty().Record(
        [
            run("r1", 1, outcome: "PassOnRetry", attempts: 2,
                failure: "System.TimeoutException", clearedBy: "RetryInFreshProcess", stallInduced: true),
            run("r2", 2, outcome: "PassOnRetry", attempts: 2,
                failure: "System.TimeoutException", clearedBy: "RetryInFreshProcess", stallInduced: true),
            run("r3", 3, outcome: "PassOnRetry", attempts: 2,
                failure: "System.TimeoutException", clearedBy: "RetryInFreshProcess", stallInduced: true)
        ]);

        ledger.ProposeHints(minOccurrences: 3).ShouldBeEmpty();
    }

    // ---- the in-process feed ------------------------------------------------------------------

    [Fact]
    public async Task a_runner_suite_feeds_the_ledger_with_wall_clocks_and_identities()
    {
        var scenario = new ScenarioDefinition("timed", [], (_, plan) =>
        {
            plan.Add(new DelegateExecutionStep("s1", StepKind.Then, "a check",
                (_, _, _) => Task.CompletedTask));
        });
        var feature = new FeatureDefinition("Ledger", typeof(Runtime.StepTimelineTests.TimelineFixture), [scenario]);

        var runner = new BobcatRunner { SuppressConsoleOutput = true };
        runner.AddFeature(feature);
        var results = await runner.RunAll();

        var observations = LedgerRuns.From(results, "run-1", t0);

        var observed = observations.ShouldHaveSingleItem();
        observed.Uid.ShouldBe("Ledger/timed");
        observed.Outcome.ShouldBe("CleanPass");
        // The #141 bracket wall clock is the duration — present because the timeline is.
        observed.TotalMs.ShouldNotBeNull();

        var durations = TestLedger.Empty().Record(observations).KnownDurations();
        durations.ShouldContainKey("Ledger/timed");
    }
}
