using Bobcat.Engine;
using Bobcat.Mtp;
using Bobcat.Resilience;
using Bobcat.Runtime;
using Microsoft.Testing.Platform.Extensions.Messages;
using Shouldly;

namespace Bobcat.Mtp.Tests;

public class SpecNodeMappingTests
{
    private static ScenarioResult result(
        Action<ExecutionResults> build,
        string[]? tags = null,
        IReadOnlyList<AttemptRecord>? attempts = null)
    {
        var results = new ExecutionResults("Scenario", DateTimeOffset.UtcNow);
        build(results);

        return new ScenarioResult("Scenario", tags ?? [], results)
        {
            Attempts = attempts ?? [new AttemptRecord(1, true, Disposition.Pass)]
        };
    }

    private static void passing(ExecutionResults results)
    {
        var step = results.StartStep("s1", 0);
        step.MarkSuccess();
        results.Counts.Rights++;
    }

    private static void failingComparison(ExecutionResults results)
    {
        var step = results.StartStep("s1", 0);
        step.StepText = "the total should be 4";
        step.MarkCells(new CellResult("result", ResultStatus.failed) { Expected = "4", Actual = "5" });

        // The executor marks a step successful whenever it completed without throwing — the
        // disagreement lives on the cell, and the count is what the mapping reads.
        step.MarkSuccess();
        results.Counts.Wrongs++;
    }

    private static void throwing(ExecutionResults results)
    {
        var step = results.StartStep("s1", 0);
        step.StepText = "the service is called";
        step.MarkErrored(new InvalidOperationException("boom"), 1);
        results.Counts.Errors++;
    }

    [Fact]
    public void uid_is_feature_qualified_so_it_is_unique_across_features()
    {
        SpecNodeMapping.Uid("Arithmetic", "adds").ShouldBe("Arithmetic/adds");
        SpecNodeMapping.Uid("Arithmetic", "adds")
            .ShouldNotBe(SpecNodeMapping.Uid("Inventory", "adds"));
    }

    [Fact]
    public void uid_matches_the_id_the_retry_budget_uses()
    {
        // One identity per scenario, everywhere. If these drifted, a supervisor's selective
        // re-run would target a different key than the retry budget tracks.
        SpecNodeMapping.Uid("Arithmetic", "adds").ShouldBe("Arithmetic/adds");
    }

    [Fact]
    public void display_name_is_feature_qualified_because_mtp_nodes_are_a_flat_list()
    {
        SpecNodeMapping.DisplayName("Arithmetic", "adds").ShouldBe("Arithmetic: adds");
    }

    [Fact]
    public void tags_become_mtp_metadata_using_the_shared_resilience_vocabulary()
    {
        var traits = SpecNodeMapping.Traits(["isolated", "recycle(rabbit)", "regression"]).ToList();

        traits.ShouldContain(t => t.Key == ResilienceTags.Isolated && t.Value == "true");
        traits.ShouldContain(t => t.Key == ResilienceTags.RecycleOnRetry && t.Value == "rabbit");
        traits.ShouldContain(t => t.Key == "regression" && t.Value == "true");
    }

    [Fact]
    public void a_passing_scenario_maps_to_the_passed_state()
    {
        SpecNodeMapping.StateFor(result(passing)).ShouldBeOfType<PassedTestNodeStateProperty>();
    }

    [Fact]
    public void a_comparison_failure_maps_to_failed_not_error()
    {
        // The failed/error split is what a supervisor's Disposition policy keys off, so Bobcat
        // has to honour it rather than reporting everything as one kind of failure.
        SpecNodeMapping.StateFor(result(failingComparison))
            .ShouldBeOfType<FailedTestNodeStateProperty>();
    }

    [Fact]
    public void an_escaped_exception_maps_to_error_and_keeps_the_original_exception()
    {
        var state = SpecNodeMapping.StateFor(result(throwing)).ShouldBeOfType<ErrorTestNodeStateProperty>();

        state.Exception.ShouldBeOfType<InvalidOperationException>();
        state.Exception!.Message.ShouldBe("boom");
    }

    [Fact]
    public void a_comparison_failure_carries_expected_and_actual_in_its_message()
    {
        var state = SpecNodeMapping.StateFor(result(failingComparison)).ShouldBeOfType<FailedTestNodeStateProperty>();

        // Cells hold the comparison result even though the step's own status is 'success' —
        // the executor marks a step successful whenever it completed without throwing.
        state.Exception!.Message.ShouldBe("the total should be 4 — result: expected 4, got 5");
    }

    [Fact]
    public void outcome_metadata_records_a_pass_on_retry_rather_than_hiding_it_in_the_pass_state()
    {
        var result = SpecNodeMappingTests.result(passing, attempts:
        [
            new AttemptRecord(1, false, Disposition.RetryInProcess("flaky")),
            new AttemptRecord(2, true, Disposition.Pass)
        ]);

        var metadata = SpecNodeMapping.OutcomeMetadata(result).ToList();

        // MTP has no "passed on retry" state, so the fact travels as metadata instead of being
        // silently collapsed into a clean pass.
        SpecNodeMapping.StateFor(result).ShouldBeOfType<PassedTestNodeStateProperty>();
        metadata.ShouldContain(m => m.Key == "bobcat.outcome" && m.Value == nameof(RunOutcome.PassOnRetry));
        metadata.ShouldContain(m => m.Key == "bobcat.attempts" && m.Value == "2");
    }

    [Fact]
    public void a_clean_pass_carries_no_attempt_count()
    {
        var metadata = SpecNodeMapping.OutcomeMetadata(result(passing)).ToList();

        metadata.ShouldContain(m => m.Key == "bobcat.outcome" && m.Value == nameof(RunOutcome.CleanPass));
        metadata.ShouldNotContain(m => m.Key == "bobcat.attempts");
    }

    [Fact]
    public void an_unhonoured_disposition_is_surfaced_as_metadata()
    {
        var result = SpecNodeMappingTests.result(failingComparison, attempts:
        [
            new AttemptRecord(1, false, Disposition.RetryInFreshProcess("isolated"))
            {
                Unsupported = "RetryInFreshProcess needs the supervisor"
            }
        ]);

        SpecNodeMapping.OutcomeMetadata(result)
            .ShouldContain(m => m.Key == "bobcat.unsupportedDisposition");
    }

    [Fact]
    public void timing_never_produces_a_negative_duration()
    {
        // EndTime is left unset when a scenario aborts early; a negative TimeSpan would make
        // the platform's own reporting nonsense.
        var results = new ExecutionResults("Scenario", DateTimeOffset.UtcNow);
        var result = new ScenarioResult("Scenario", [], results);

        SpecNodeMapping.TimingFor(result).GlobalTiming.Duration.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }
}
