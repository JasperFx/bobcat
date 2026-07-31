using JasperFx.Testing;
using Bobcat.Engine;
using Bobcat.Resilience;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Resilience;

/// <summary>
/// The retry loop end to end through <see cref="BobcatRunner"/>: does a tagged flaky scenario
/// actually get another attempt, does an untagged one not, and does the report tell the truth
/// about which happened.
/// </summary>
public class RunnerRetryTests
{
    /// <summary>Counts attempts per scenario so a step can fail the first N times.</summary>
    private static readonly Dictionary<string, int> attempts = new();

    public class FlakyFixture : Fixture;

    private static FeatureDefinition feature(
        string scenarioTitle,
        string[] tags,
        int failuresBeforePassing,
        Action<StepResult>? failWith = null)
    {
        var key = Guid.NewGuid().ToString();
        attempts[key] = 0;

        var scenario = new ScenarioDefinition(scenarioTitle, tags, (_, plan) =>
        {
            plan.Add(new DelegateExecutionStep("step-1", StepKind.Then, "the flaky step runs",
                (_, result, _) =>
                {
                    var attempt = ++attempts[key];
                    if (attempt <= failuresBeforePassing)
                    {
                        if (failWith is not null) failWith(result);
                        else result.MarkFailed();
                    }

                    return Task.CompletedTask;
                }));
        });

        return new FeatureDefinition("Flaky Feature", typeof(FlakyFixture), [scenario]);
    }

    private static BobcatRunner runner(FeatureDefinition feature, RetryBudget budget)
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true, RetryBudget = budget };
        runner.AddFeature(feature);
        return runner;
    }

    [Fact]
    public async Task an_untagged_failure_is_not_retried_even_when_the_budget_allows_it()
    {
        // Opt-in is the whole safety property: a budget being available must not silently start
        // retrying every failing test.
        var results = await runner(
            feature("untagged", [], failuresBeforePassing: 1),
            new RetryBudget { MaxAttemptsPerTest = 3 }).RunAll();

        var scenario = results.AllScenarios.Single();
        scenario.AttemptCount.ShouldBe(1);
        scenario.Outcome.ShouldBe(RunOutcome.Failed);
        results.ExitCode.ShouldBe(1);
    }

    [Fact]
    public async Task a_tagged_scenario_that_recovers_is_reported_as_passed_on_retry_not_a_clean_pass()
    {
        var results = await runner(
            feature("flaky", ["retry(2)"], failuresBeforePassing: 1),
            new RetryBudget { MaxAttemptsPerTest = 2 }).RunAll();

        var scenario = results.AllScenarios.Single();

        scenario.AttemptCount.ShouldBe(2);
        scenario.Outcome.ShouldBe(RunOutcome.PassOnRetry);
        scenario.Outcome.ShouldNotBe(RunOutcome.CleanPass);

        // It passed, so the build stays green — but the flakiness is on the record.
        results.ExitCode.ShouldBe(0);
        results.PassedOnRetry.Single().Title.ShouldBe("flaky");
        results.RetriesPerformed.ShouldBe(1);
    }

    [Fact]
    public async Task a_first_time_pass_is_a_clean_pass_and_appears_in_no_ledger()
    {
        var results = await runner(
            feature("solid", ["retry(2)"], failuresBeforePassing: 0),
            new RetryBudget { MaxAttemptsPerTest = 2 }).RunAll();

        var scenario = results.AllScenarios.Single();
        scenario.Outcome.ShouldBe(RunOutcome.CleanPass);
        scenario.WasRetried.ShouldBeFalse();
        results.PassedOnRetry.ShouldBeEmpty();
        results.RetriesPerformed.ShouldBe(0);
    }

    [Fact]
    public async Task retries_stop_at_the_budget_and_the_scenario_still_fails()
    {
        // Fails 5 times but is only allowed 3 attempts.
        var results = await runner(
            feature("hopeless", ["retry(3)"], failuresBeforePassing: 5),
            new RetryBudget { MaxAttemptsPerTest = 3 }).RunAll();

        var scenario = results.AllScenarios.Single();

        scenario.AttemptCount.ShouldBe(3);
        scenario.Outcome.ShouldBe(RunOutcome.Failed);
        results.ExitCode.ShouldBe(1);

        // The last attempt records WHY it stopped rather than just failing silently.
        scenario.Attempts.Last().Disposition.Reason.ShouldContain("used all 3 allowed attempts");
    }

    [Fact]
    public async Task a_disposition_needing_the_supervisor_is_recorded_as_not_honoured_rather_than_downgraded()
    {
        // @isolated asks for a fresh process. That machinery does not exist yet, so the run must
        // say so — quietly retrying in-process instead would report work that never happened.
        var results = await runner(
            feature("needs-isolation", ["retry(2)", "isolated"], failuresBeforePassing: 1),
            new RetryBudget { MaxAttemptsPerTest = 2 }).RunAll();

        var scenario = results.AllScenarios.Single();

        scenario.AttemptCount.ShouldBe(1);
        scenario.Outcome.ShouldBe(RunOutcome.Failed);

        var unsupported = scenario.UnsupportedDispositions.Single();
        unsupported.ShouldContain("RetryInFreshProcess");
        unsupported.ShouldContain("NOT retried");

        results.UnsupportedDispositions.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task a_catastrophic_failure_aborts_the_run_and_is_never_retried()
    {
        var results = await runner(
            feature("doomed", ["retry(5)"], failuresBeforePassing: 5,
                failWith: result => result.MarkErrored(new SpecCatastrophicException("host is gone"), 0)),
            new RetryBudget { MaxAttemptsPerTest = 5 }).RunAll();

        var scenario = results.AllScenarios.Single();

        scenario.AttemptCount.ShouldBe(1);
        scenario.Attempts.Single().Disposition.Kind.ShouldBe(DispositionKind.AbortRun);
        scenario.Outcome.ShouldBe(RunOutcome.Aborted);
        results.ExitCode.ShouldBe(2);
    }

    [Fact]
    public async Task a_custom_policy_can_opt_a_scenario_into_retrying_without_any_tag()
    {
        var feature = RunnerRetryTests.feature("untagged-but-known-flaky", [], failuresBeforePassing: 1);

        var runner = new BobcatRunner { SuppressConsoleOutput = true, RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 } };
        runner.AddFeature(feature);
        runner.AddFailurePolicy(new RetryEverythingOnce());

        var results = await runner.RunAll();

        results.AllScenarios.Single().Outcome.ShouldBe(RunOutcome.PassOnRetry);
    }

    [Fact]
    public async Task the_observer_is_told_about_a_retry_as_it_happens()
    {
        // Renderers hang off this. A retry that surfaces only in the final summary reads as a
        // clean pass while the run is still in flight.
        var observer = new RecordingObserver();

        var runner = new BobcatRunner { SuppressConsoleOutput = true, RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 } };
        runner.AddFeature(feature("watched", ["retry(2)"], failuresBeforePassing: 1));
        runner.WithObserver(observer);

        await runner.RunAll();

        var (title, attempt, reason) = observer.Retries.Single();
        title.ShouldBe("watched");
        attempt.ShouldBe(2);
        reason.ShouldContain("@retry");
    }

    private class RecordingObserver : IExecutionObserver
    {
        public List<(string Title, int Attempt, string Reason)> Retries { get; } = [];

        public void ScenarioRetrying(string scenarioTitle, int nextAttempt, string reason)
            => Retries.Add((scenarioTitle, nextAttempt, reason));

        public void FeatureStarted(string featureTitle) { }
        public void FeatureFinished(string featureTitle) { }
        public void ScenarioStarted(string featureTitle, string scenarioTitle) { }
        public void StepStarted(string stepId, StepKind kind, string stepText) { }
        public void StepProgress(string stepId, StepUpdate update) { }
        public void StepFinished(StepResult result) { }
        public void ScenarioFinished(ExecutionResults results) { }
    }

    private class RetryEverythingOnce : IFailurePolicy
    {
        public Disposition? Decide(AttemptContext attempt)
            => attempt is { Succeeded: false, RetriesAvailable: true }
                ? Disposition.RetryInProcess("policy retries everything once")
                : null;
    }
}
