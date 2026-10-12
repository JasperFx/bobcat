using Bobcat.Engine;

namespace Bobcat.Projected.Acceptance.Tests;

/// <summary>
/// bobcat#484: a step helper that is never intercepted runs as a plain method call, so no step opens
/// and a <see cref="SpecAssert"/> inside it has nothing to hang its wrong on. That is every helper in a
/// project without the <c>Bobcat.Generated</c> interceptor opt-in (or without the generator at all),
/// and such a spec used to pass with every wrong dropped.
/// </summary>
/// <remarks>
/// This project IS opted in, so the un-intercepted call is made the one way the generator cannot see:
/// through a delegate. An interceptor replaces an invocation of the method itself, and invoking a
/// delegate is an invocation of <c>Invoke</c>, so the helper runs exactly as it would in a misconfigured
/// project. The intercepted twin beside it is the control.
/// </remarks>
public class UninterceptedStepTests
{
    public class BalanceGrammar
    {
        [Then("the balance is {balance}")]
        internal void TheBalanceIs(int balance) => SpecAssert.Check("balance", 100, balance);
    }

    private readonly BalanceGrammar _grammar = new();

    [Fact]
    public void an_intercepted_helper_records_its_wrong_on_its_step()
    {
        ScenarioRecorder.Begin("Projected Acceptance", "Intercepted", null, Guid.NewGuid());
        _grammar.TheBalanceIs(150);

        var recording = ScenarioRecorder.Current.ShouldNotBeNull();
        recording.Step("the balance is 150").Status.ShouldBe(ResultStatus.failed);
        recording.SteplessWrongs.ShouldBeEmpty();

        MarkerStepRun.EndScenario(ScenarioVerdict.Passed).ShouldBeOfType<SpecAssertionException>();
    }

    [Fact]
    public void an_unintercepted_helper_fails_the_scenario_instead_of_passing_vacuously()
    {
        Action<int> uninterceptedCall = _grammar.TheBalanceIs;

        ScenarioRecorder.Begin("Projected Acceptance", "Not intercepted", null, Guid.NewGuid());
        uninterceptedCall(150);

        var recording = ScenarioRecorder.Current.ShouldNotBeNull();

        // No step opened, which is the whole symptom
        recording.Steps.ShouldBeEmpty();
        recording.SteplessWrongs.ShouldHaveSingleItem().ShouldBe("balance: expected '150', got '100'");

        // The test method itself threw nothing, so the runner was about to call it green. The adapter
        // throws what EndScenario hands back, and the spec goes red.
        var failure = MarkerStepRun.EndScenario(ScenarioVerdict.Passed).ShouldBeOfType<SpecAssertionException>();
        failure.Message.ShouldContain("1 SpecAssert wrong was reported with no step open");
        failure.Message.ShouldContain("InterceptorsNamespaces");
    }

    [Fact]
    public void an_unintercepted_helper_whose_checks_hold_still_passes()
    {
        Action<int> uninterceptedCall = _grammar.TheBalanceIs;

        ScenarioRecorder.Begin("Projected Acceptance", "Not intercepted, right value", null, Guid.NewGuid());
        uninterceptedCall(100);

        MarkerStepRun.EndScenario(ScenarioVerdict.Passed).ShouldBeNull();
    }
}
