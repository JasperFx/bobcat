using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Bobcat.MSTest.Tests;

/// <summary>
/// Issue #386: the adapter under the runner it is written for. These pass or fail on whether MSTest
/// really runs the shipped global hooks around a test, and flows what they set into its body.
/// </summary>
[TestClass, BobcatFeature("Async daemon"), BobcatScenario]
public class LiveScenarioBracketTests
{
    [TestMethod]
    public void a_scenario_is_open_and_named_while_the_body_runs()
        => ScenarioRecorder.Current.ShouldNotBeNull()
            .Uid.ShouldBe("Async daemon/a scenario is open and named while the body runs");

    [TestMethod]
    public async Task the_scenario_survives_an_await()
    {
        // The recording is AsyncLocal, set in [GlobalTestInitialize]. MSTest has to flow that
        // execution context into the test and on across its continuations.
        await Task.Delay(10);
        await Task.Yield();

        ScenarioRecorder.Current.ShouldNotBeNull()
            .Uid.ShouldBe("Async daemon/the scenario survives an await");
    }

    [TestMethod]
    public void a_step_recorded_in_the_body_lands_on_the_scenario()
    {
        using (ScenarioRecorder.Step("Given", "the events are published"))
        {
        }

        ScenarioRecorder.Current!.Steps.ShouldHaveSingleItem()
            .ToString().ShouldBe("Given the events are published");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void a_data_driven_test_opens_a_scenario_per_row(int row)
        => ScenarioRecorder.Current.ShouldNotBeNull()
            .Uid.ShouldBe("Async daemon/a data driven test opens a scenario per row");
}

/// <summary>Opting in one method at a time, and the class's other tests left alone.</summary>
[TestClass]
public class MethodLevelOptInTests
{
    [TestMethod, BobcatScenario]
    public void the_marked_method_opens_a_scenario()
        => ScenarioRecorder.Current.ShouldNotBeNull()
            .Uid.ShouldBe("Method Level Opt In Tests/the marked method opens a scenario");

    [TestMethod]
    public void an_unmarked_method_does_not()
        // The hooks run around every test in the assembly; only an opted-in one records.
        => ScenarioRecorder.Current.ShouldBeNull();
}

/// <summary>A class that never opted in is invisible to Bobcat.</summary>
[TestClass]
public class NotOptedInTests
{
    [TestMethod]
    public void no_scenario_is_open()
        => ScenarioRecorder.Current.ShouldBeNull();
}

/// <summary>
/// Scenarios open at once on one shared set of hooks must each see their own recording. The barrier
/// makes the overlap certain rather than likely: none of these gets past it until all three are in
/// flight, so if MSTest ran them one at a time this fails instead of passing by accident.
/// </summary>
[TestClass, BobcatScenario]
public class ParallelScenarioTests
{
    private static readonly Barrier _allInFlight = new(3);

    [TestMethod] public Task first() => holdsItsOwnRecording("first");
    [TestMethod] public Task second() => holdsItsOwnRecording("second");
    [TestMethod] public Task third() => holdsItsOwnRecording("third");

    private static async Task holdsItsOwnRecording(string name)
    {
        await Task.Run(() => _allInFlight.SignalAndWait(TimeSpan.FromSeconds(30)).ShouldBeTrue(
            "the three tests never ran concurrently, so this proves nothing about parallel safety"));

        await Task.Delay(10);
        ScenarioRecorder.Current.ShouldNotBeNull().Uid.ShouldBe($"Parallel Scenario Tests/{name}");
    }
}
