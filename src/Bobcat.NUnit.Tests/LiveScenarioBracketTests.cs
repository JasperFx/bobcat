using NUnit.Framework;
using Shouldly;

namespace Bobcat.NUnit.Tests;

/// <summary>
/// Issue #385: the adapter under the runner it is written for. These pass or fail on whether NUnit
/// really runs the action around each test when it sits on the class, and flows what it set into
/// the body.
/// </summary>
[BobcatFeature("Async daemon"), BobcatScenario]
public class LiveScenarioBracketTests
{
    [Test]
    public void a_scenario_is_open_and_named_while_the_body_runs()
        => ScenarioRecorder.Current.ShouldNotBeNull()
            .Uid.ShouldBe("Async daemon/a scenario is open and named while the body runs");

    [Test]
    public async Task the_scenario_survives_an_await()
    {
        // The recording is AsyncLocal, set in BeforeTest. NUnit has to flow that execution
        // context into the test and on across its continuations.
        await Task.Delay(10);
        await Task.Yield();

        ScenarioRecorder.Current.ShouldNotBeNull()
            .Uid.ShouldBe("Async daemon/the scenario survives an await");
    }

    [Test]
    public void a_step_recorded_in_the_body_lands_on_the_scenario()
    {
        using (ScenarioRecorder.Step("Given", "the events are published"))
        {
        }

        ScenarioRecorder.Current!.Steps.ShouldHaveSingleItem()
            .ToString().ShouldBe("Given the events are published");
    }

    [TestCase(1)]
    [TestCase(2)]
    public void a_test_case_opens_a_scenario_per_case(int row)
        => ScenarioRecorder.Current.ShouldNotBeNull()
            .Uid.ShouldBe("Async daemon/a test case opens a scenario per case");
}

/// <summary>Opting in one method at a time, and the class's other tests left alone.</summary>
public class MethodLevelOptInTests
{
    [Test, BobcatScenario]
    public void the_marked_method_opens_a_scenario()
        => ScenarioRecorder.Current.ShouldNotBeNull()
            .Uid.ShouldBe("Method Level Opt In Tests/the marked method opens a scenario");

    [Test]
    public void an_unmarked_method_does_not()
        => ScenarioRecorder.Current.ShouldBeNull();
}

/// <summary>
/// Scenarios open at once under one shared attribute instance — NUnit builds one per declaration and
/// runs it around every test in the class — must each see their own recording. The barrier makes the
/// overlap certain rather than likely: none of these gets past it until all three are in flight, so if
/// NUnit ran them one at a time this fails instead of passing by accident.
/// </summary>
[BobcatScenario, Parallelizable(ParallelScope.All)]
public class ParallelScenarioTests
{
    private static readonly Barrier _allInFlight = new(3);

    [Test] public Task first() => holdsItsOwnRecording("first");
    [Test] public Task second() => holdsItsOwnRecording("second");
    [Test] public Task third() => holdsItsOwnRecording("third");

    private static async Task holdsItsOwnRecording(string name)
    {
        await Task.Run(() => _allInFlight.SignalAndWait(TimeSpan.FromSeconds(30)).ShouldBeTrue(
            "the three tests never ran concurrently, so this proves nothing about parallel safety"));

        await Task.Delay(10);
        ScenarioRecorder.Current.ShouldNotBeNull().Uid.ShouldBe($"Parallel Scenario Tests/{name}");
    }
}
