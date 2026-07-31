using JasperFx.Testing;
using Bobcat.Engine;
using Bobcat.Resilience;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Resilience;

/// <summary>
/// Author-declared hints end to end through <see cref="BobcatRunner"/> — does an attribute on a
/// fixture actually change what the retry loop does, and does the report say why.
/// </summary>
public class RunnerRecoveryHintTests
{
    private static readonly Dictionary<string, int> attempts = new();

    [ClearsOnRetry(typeof(TimeoutException), Because = "the broker is slow to warm up")]
    public class BrokerFixture : Fixture;

    [NeverRecovers(typeof(NotSupportedException), Because = "this is a real bug")]
    public class DeterministicFixture : Fixture;

    public class PlainFixture : Fixture;

    private static FeatureDefinition feature(
        Type fixtureType,
        string[] tags,
        Func<int, Exception?> failure,
        string featureTitle = "Hinted Feature")
    {
        var key = Guid.NewGuid().ToString();
        attempts[key] = 0;

        var scenario = new ScenarioDefinition("the flaky step runs", tags, (_, plan) =>
        {
            plan.Add(new DelegateExecutionStep("step-1", StepKind.Then, "the flaky step runs",
                (_, _, _) =>
                {
                    var thrown = failure(++attempts[key]);
                    return thrown is null ? Task.CompletedTask : Task.FromException(thrown);
                }));
        });

        return new FeatureDefinition(featureTitle, fixtureType, [scenario]);
    }

    private static async Task<ScenarioResult> run(FeatureDefinition feature, RetryBudget budget)
    {
        var runner = new BobcatRunner { SuppressConsoleOutput = true, RetryBudget = budget };
        runner.AddFeature(feature);

        var results = await runner.RunAll();
        return results.Features.Single().Scenarios.Single();
    }

    [Fact]
    public async Task a_hinted_failure_is_retried_although_the_scenario_carries_no_retry_tag()
    {
        // The declaration lives on the fixture, not in the Gherkin — so an assertion failure on
        // the same scenario is still reported as the bug it is.
        var result = await run(
            feature(typeof(BrokerFixture), [], attempt => attempt == 1 ? new TimeoutException() : null),
            new RetryBudget { MaxAttemptsPerTest = 2 });

        result.Attempts.Count.ShouldBe(2);
        result.Outcome.ShouldBe(RunOutcome.PassOnRetry);
        result.Attempts[0].Disposition.Reason.ShouldContain("the broker is slow to warm up");
    }

    [Fact]
    public async Task a_failure_the_hint_does_not_describe_is_still_not_retried()
    {
        // Mutation guard: the hint must be matching on the failure class, not simply enabling
        // retries for every failure on a hinted fixture.
        var result = await run(
            feature(typeof(BrokerFixture), [], _ => new InvalidOperationException("something else")),
            new RetryBudget { MaxAttemptsPerTest = 3 });

        result.Attempts.Count.ShouldBe(1);
        result.Outcome.ShouldBe(RunOutcome.Failed);
    }

    [Fact]
    public async Task a_fixture_with_no_hints_behaves_exactly_as_before()
    {
        var result = await run(
            feature(typeof(PlainFixture), [], attempt => attempt == 1 ? new TimeoutException() : null),
            new RetryBudget { MaxAttemptsPerTest = 3 });

        result.Attempts.Count.ShouldBe(1);
        result.Outcome.ShouldBe(RunOutcome.Failed);
    }

    [Fact]
    public async Task a_never_recovers_hint_stops_a_tagged_scenario_from_burning_its_attempts()
    {
        var result = await run(
            feature(typeof(DeterministicFixture), ["retry(3)"], _ => new NotSupportedException()),
            new RetryBudget { MaxAttemptsPerTest = 3 });

        result.Attempts.Count.ShouldBe(1);
        result.Attempts[0].Disposition.Reason.ShouldContain("this is a real bug");
    }

    [Fact]
    public async Task the_same_tagged_scenario_does_retry_when_the_failure_is_not_the_hinted_one()
    {
        // The control for the test above: @retry(3) still works on that fixture.
        var result = await run(
            feature(typeof(DeterministicFixture), ["retry(3)"],
                attempt => attempt == 1 ? new InvalidOperationException() : null),
            new RetryBudget { MaxAttemptsPerTest = 3 });

        result.Attempts.Count.ShouldBe(2);
        result.Outcome.ShouldBe(RunOutcome.PassOnRetry);
    }

    [Fact]
    public async Task a_hint_is_scoped_to_the_feature_its_fixture_owns()
    {
        // Two features, one hinted. The unhinted one must not inherit the retry.
        var runner = new BobcatRunner
        {
            SuppressConsoleOutput = true,
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 }
        };

        runner.AddFeature(feature(typeof(BrokerFixture), [],
            attempt => attempt == 1 ? new TimeoutException() : null, "Hinted Feature"));
        runner.AddFeature(feature(typeof(PlainFixture), [],
            attempt => attempt == 1 ? new TimeoutException() : null, "Plain Feature"));

        var results = await runner.RunAll();

        results.Features.Single(f => f.Title == "Hinted Feature")
            .Scenarios.Single().Attempts.Count.ShouldBe(2);

        results.Features.Single(f => f.Title == "Plain Feature")
            .Scenarios.Single().Attempts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_hint_cannot_retry_a_run_whose_budget_allows_nothing()
    {
        // Hints describe failures; the budget decides how much time the run may spend. An
        // unconfigured run still retries nothing, however many hints are declared.
        var result = await run(
            feature(typeof(BrokerFixture), [], attempt => attempt == 1 ? new TimeoutException() : null),
            RetryBudget.None);

        result.Attempts.Count.ShouldBe(1);
        result.Attempts[0].Disposition.Reason.ShouldContain("clears on retry");
    }
}
