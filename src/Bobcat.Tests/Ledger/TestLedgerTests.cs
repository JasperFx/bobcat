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
    public void merging_keeps_the_retention_of_the_ledger_merged_into()
    {
        // Issue #698: keeping the larger retention meant one artifact written at the default 20
        // grew a committed file kept at 3 for good.
        var committed = TestLedger.Empty(maxRunsPerTest: 3)
            .Record(Enumerable.Range(1, 3).Select(day => run($"c{day}", day)));
        var artifact = TestLedger.Empty(maxRunsPerTest: 20)
            .Record(Enumerable.Range(4, 5).Select(day => run($"a{day}", day)));

        var merged = committed.Merge(artifact);
        merged.MaxRunsPerTest.ShouldBe(3);
        merged.Tests["F/spec"].Select(r => r.RunId).ShouldBe(["a8", "a7", "a6"]);

        // Merging again, as the next fold would, does not ratchet it either.
        merged.Merge(artifact).MaxRunsPerTest.ShouldBe(3);
        artifact.Merge(committed).MaxRunsPerTest.ShouldBe(20);
    }

    [Fact]
    public void a_ledgers_retention_is_changed_by_merging_it_into_an_empty_one()
    {
        var ledger = TestLedger.Empty(maxRunsPerTest: 20)
            .Record(Enumerable.Range(1, 5).Select(day => run($"r{day}", day)));

        var smaller = TestLedger.Empty(maxRunsPerTest: 2).Merge(ledger);

        smaller.MaxRunsPerTest.ShouldBe(2);
        TestLedger.FromJson(smaller.ToJson()).MaxRunsPerTest.ShouldBe(2);
        smaller.Tests["F/spec"].Select(r => r.RunId).ShouldBe(["r5", "r4"]);
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

    // ---- loading at a retention (issue #746) ----------------------------------------------------
    private static string savedAt(int maxRunsPerTest, int runs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bobcat-ledger-{Guid.NewGuid():N}", "ledger.json");
        TestLedger.Empty(maxRunsPerTest)
            .Record(Enumerable.Range(1, runs).Select(i => run($"r{i}", i)))
            .Save(path);
        return path;
    }

    private static void deleteLedger(string path)
    {
        try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
    }

    [Fact]
    public void loading_an_existing_file_at_a_larger_retention_raises_it()
    {
        var path = savedAt(maxRunsPerTest: 3, runs: 3);
        try
        {
            var ledger = TestLedger.Load(path, 5);
            ledger.MaxRunsPerTest.ShouldBe(5);

            ledger.Record([run("r4", 4), run("r5", 5)]).Save(path);

            var reloaded = TestLedger.Load(path);
            reloaded.MaxRunsPerTest.ShouldBe(5);
            reloaded.Tests["F/spec"].Select(r => r.RunId).ShouldBe(["r5", "r4", "r3", "r2", "r1"]);
        }
        finally
        {
            deleteLedger(path);
        }
    }

    [Fact]
    public void loading_an_existing_file_at_a_smaller_retention_prunes_to_it()
    {
        var path = savedAt(maxRunsPerTest: 5, runs: 5);
        try
        {
            var ledger = TestLedger.Load(path, 3);
            ledger.MaxRunsPerTest.ShouldBe(3);
            ledger.Tests["F/spec"].Select(r => r.RunId).ShouldBe(["r5", "r4", "r3"]);

            ledger.Save(path);
            TestLedger.Load(path).MaxRunsPerTest.ShouldBe(3);
        }
        finally
        {
            deleteLedger(path);
        }
    }

    [Fact]
    public void loading_without_a_retention_keeps_the_files_own()
    {
        var path = savedAt(maxRunsPerTest: 3, runs: 2);
        try
        {
            TestLedger.Load(path).MaxRunsPerTest.ShouldBe(3);
            TestLedger.Load(Path.Combine(Path.GetDirectoryName(path)!, "missing.json"), 7).MaxRunsPerTest.ShouldBe(7);
        }
        finally
        {
            deleteLedger(path);
        }
    }

    // ---- the compact form (issue #698) ----------------------------------------------------------

    [Fact]
    public void the_file_is_unindented_and_names_each_test_once()
    {
        var ledger = TestLedger.Empty().Record(
        [
            run("r1", 1, "A/one"), run("r2", 2, "A/one"), run("r3", 3, "A/one")
        ]);

        var text = ledger.ToJson();

        text.ShouldNotContain("\n");
        text.ShouldNotContain("  ");
        text.ShouldStartWith("{\"version\":3,");
        // The key is written once and nothing else; a display name equal to it is not written at all.
        countOf(text, "A/one").ShouldBe(1);
        text.ShouldNotContain("displayName");
        text.ShouldNotContain("uid");
        // A false flag is the default and is left out.
        text.ShouldNotContain("stallInduced");
    }

    [Fact]
    public void a_test_keyed_by_uid_stores_its_display_name_once_and_it_is_the_newest_observations()
    {
        var ledger = TestLedger.Empty().Record(
        [
            run("r1", 1, "A/one") with { DisplayName = "Old.Name.one", Key = "A/one" },
            run("r2", 2, "A/one") with { DisplayName = "Ns.Class.one", Key = "A/one" },
            run("r3", 3, "A/one") with { DisplayName = "Ns.Class.one", Key = "A/one" }
        ]);

        var text = ledger.ToJson();
        countOf(text, "Ns.Class.one").ShouldBe(1);
        text.ShouldNotContain("Old.Name.one");

        var loaded = TestLedger.FromJson(text);
        loaded.Tests["A/one"].ShouldAllBe(r => r.DisplayName == "Ns.Class.one");
        loaded.ToJson().ShouldBe(text);
    }

    [Fact]
    public void every_field_of_an_observation_survives_the_compact_form()
    {
        var observation = run("r1", 1, "A/one", totalMs: 250, outcome: "PassOnRetry", attempts: 2,
            failure: "System.TimeoutException", clearedBy: "RetryInProcess", stallInduced: true)
            with { DisplayName = "Ns.Class.one", FirstMs = 50 };

        var loaded = TestLedger.FromJson(TestLedger.Empty().Record([observation]).ToJson());

        // The uid is not kept for a test keyed by display name: it is the per-machine hash the
        // key exists to get away from, so the key stands in for it.
        loaded.Tests["Ns.Class.one"].ShouldHaveSingleItem()
            .ShouldBe(observation with { Uid = "Ns.Class.one", Key = "Ns.Class.one" });
    }

    [Fact]
    public void a_version_1_file_still_loads_and_is_written_back_compact()
    {
        const string version1 = """
            {
              "version": 1,
              "maxRunsPerTest": 3,
              "tests": {
                "A/one": [
                  {
                    "runId": "r2",
                    "at": "2026-08-03T12:00:00+00:00",
                    "uid": "A/one",
                    "displayName": "Ns.Class.one",
                    "outcome": "CleanPass",
                    "attempts": 1,
                    "totalMs": 120,
                    "firstMs": 120,
                    "stallInduced": false
                  }
                ]
              }
            }
            """;

        var ledger = TestLedger.FromJson(version1);

        ledger.MaxRunsPerTest.ShouldBe(3);
        // Re-keyed by display name (issue #730).
        var observation = ledger.Tests["Ns.Class.one"].ShouldHaveSingleItem();
        observation.DisplayName.ShouldBe("Ns.Class.one");
        observation.TotalMs.ShouldBe(120);
        ledger.ToJson().ShouldStartWith("{\"version\":3,");
    }

    // ---- the display-name key (issue #730) -----------------------------------------------------

    private static LedgerRun observed(string runId, int day, string uid, string displayName, long totalMs = 100)
        => new(runId, t0.AddDays(day), uid, displayName, "CleanPass", 1) { TotalMs = totalMs, FirstMs = totalMs };

    [Fact]
    public void one_test_observed_under_different_uids_on_different_machines_is_one_test()
    {
        // xUnit v3's uid hashes the assembly path: a laptop and a CI runner disagree on every uid.
        var laptop = TestLedger.Empty().Record([observed("r1", 1, "hash-laptop", "Ns.Class.one", 100)]);
        var ci = TestLedger.Empty().Record([observed("r2", 2, "hash-ci", "Ns.Class.one", 300)]);

        var merged = laptop.Merge(ci);

        merged.Tests.Keys.ShouldBe(["Ns.Class.one"]);
        merged.Tests["Ns.Class.one"].Count.ShouldBe(2);
        merged.KnownDurations()["Ns.Class.one"].ShouldBe(TimeSpan.FromMilliseconds(200));
        merged.SharedDisplayNames.ShouldBeEmpty();
    }

    [Fact]
    public void two_tests_sharing_a_display_name_in_one_run_are_keyed_by_uid_and_never_merged()
    {
        var keys = LedgerKeys.For([("u1", "Ns.Class.same"), ("u2", "Ns.Class.same"), ("u3", "Ns.Class.other")]);
        keys["u1"].ShouldBe("u1");
        keys["u2"].ShouldBe("u2");
        keys["u3"].ShouldBe("Ns.Class.other");

        var ledger = TestLedger.Empty().Record(
        [
            observed("r1", 1, "u1", "Ns.Class.same", 100) with { Key = keys["u1"] },
            observed("r1", 1, "u2", "Ns.Class.same", 9_000) with { Key = keys["u2"] },
            observed("r1", 1, "u3", "Ns.Class.other") with { Key = keys["u3"] }
        ]);

        ledger.Tests.Keys.ShouldBe(["Ns.Class.other", "u1", "u2"]);
        ledger.KnownDurations()["u1"].ShouldBe(TimeSpan.FromMilliseconds(100));
        ledger.KnownDurations()["u2"].ShouldBe(TimeSpan.FromMilliseconds(9_000));
        // The collision is recorded, in memory and in the file.
        ledger.SharedDisplayNames.ShouldBe(["Ns.Class.same"]);
        TestLedger.FromJson(ledger.ToJson()).SharedDisplayNames.ShouldBe(["Ns.Class.same"]);
        TestLedger.FromJson(ledger.ToJson()).ToJson().ShouldBe(ledger.ToJson());
    }

    [Fact]
    public void observations_recorded_without_keys_still_never_merge_two_tests()
    {
        // A feed that did not use LedgerKeys: two uids under one display name in one run.
        var ledger = TestLedger.Empty().Record(
        [
            observed("r1", 1, "u1", "Ns.Class.same", 100),
            observed("r1", 1, "u2", "Ns.Class.same", 9_000)
        ]);

        ledger.Tests.Keys.ShouldBe(["u1", "u2"]);
        ledger.SharedDisplayNames.ShouldBe(["Ns.Class.same"]);
    }

    [Fact]
    public void a_blank_display_name_keys_on_the_uid()
    {
        var keys = LedgerKeys.For([("u1", ""), ("u2", " ")]);
        keys["u1"].ShouldBe("u1");
        keys["u2"].ShouldBe("u2");

        TestLedger.Empty().Record([observed("r1", 1, "u1", "")]).Tests.Keys.ShouldBe(["u1"]);
    }

    [Fact]
    public void a_uid_keyed_version_2_file_is_re_keyed_by_display_name()
    {
        // Two machines' uids for one test fold into one; two uids sharing a display name in the
        // same run were two tests and stay apart.
        const string version2 = """
            {"version":2,"maxRunsPerTest":5,"tests":{
              "hash-a":{"displayName":"Ns.Class.one","runs":[{"runId":"r1","at":"2026-08-02T12:00:00+00:00","outcome":"CleanPass","attempts":1,"totalMs":100}]},
              "hash-b":{"displayName":"Ns.Class.one","runs":[{"runId":"r2","at":"2026-08-03T12:00:00+00:00","outcome":"CleanPass","attempts":1,"totalMs":300}]},
              "hash-c":{"displayName":"Ns.Class.same","runs":[{"runId":"r1","at":"2026-08-02T12:00:00+00:00","outcome":"CleanPass","attempts":1,"totalMs":10}]},
              "hash-d":{"displayName":"Ns.Class.same","runs":[{"runId":"r1","at":"2026-08-02T12:00:00+00:00","outcome":"CleanPass","attempts":1,"totalMs":20}]},
              "A/spec":{"runs":[{"runId":"r1","at":"2026-08-02T12:00:00+00:00","outcome":"CleanPass","attempts":1,"totalMs":50}]}
            }}
            """;

        var ledger = TestLedger.FromJson(version2);

        ledger.Tests.Keys.ShouldBe(["A/spec", "Ns.Class.one", "hash-c", "hash-d"]);
        ledger.Tests["Ns.Class.one"].Select(r => r.RunId).ShouldBe(["r2", "r1"]);
        ledger.SharedDisplayNames.ShouldBe(["Ns.Class.same"]);
        ledger.ToJson().ShouldStartWith("{\"version\":3,");
        TestLedger.FromJson(ledger.ToJson()).ToJson().ShouldBe(ledger.ToJson());
    }

    [Fact]
    public void the_default_retention_is_small_because_the_ledger_travels_as_a_ci_artifact()
    {
        TestLedger.Empty().MaxRunsPerTest.ShouldBe(5);
        TestLedger.Load(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json")).MaxRunsPerTest.ShouldBe(5);
    }

    [Fact]
    public void a_file_newer_than_this_bobcat_is_refused_by_name()
    {
        var ex = Should.Throw<BobcatConfigurationException>(
            () => TestLedger.FromJson("""{"version":99,"maxRunsPerTest":3,"tests":{}}"""));
        ex.Message.ShouldContain("version 99");
    }

    private static int countOf(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal)) count++;
        return count;
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
        var ledger = TestLedger.Empty(maxRunsPerTest: 20).Record(
        [
            run("r1", 1, totalMs: 2_000), run("r2", 2, totalMs: 2_100), run("r3", 3, totalMs: 1_900),
            run("r4", 4, totalMs: 39_000), run("r5", 5, totalMs: 41_000), run("r6", 6, totalMs: 40_000),
            // A steady test earns silence.
            run("s1", 1, "F/steady", 500), run("s2", 2, "F/steady", 510), run("s3", 3, "F/steady", 490),
            run("s4", 4, "F/steady", 505), run("s5", 5, "F/steady", 495), run("s6", 6, "F/steady", 500)
        ]);

        var trend = ledger.Trends().ShouldHaveSingleItem();
        trend.Key.ShouldBe("F/spec");
        trend.Then.ShouldBe(TimeSpan.FromMilliseconds(2_000));
        trend.Now.ShouldBe(TimeSpan.FromMilliseconds(40_000));
        trend.GrowthFactor.ShouldBe(20);
    }

    [Fact]
    public void a_trend_can_be_stated_within_the_default_retention()
    {
        var ledger = TestLedger.Empty().Record(
        [
            run("r1", 1, totalMs: 2_000), run("r2", 2, totalMs: 2_000),
            run("r3", 3, totalMs: 40_000), run("r4", 4, totalMs: 40_000), run("r5", 5, totalMs: 40_000)
        ]);

        ledger.Trends().ShouldHaveSingleItem().Key.ShouldBe("F/spec");
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
}
