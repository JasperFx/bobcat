using System.Runtime.CompilerServices;
using Bobcat.Monitoring;
using Shouldly;
using Xunit;

namespace Bobcat.Xunit.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Keep the suite hermetic: without this every first scenario probes localhost for a console
    /// that is not there. <c>BOBCAT_MONITOR=0</c> is the documented hard opt-out and suppresses
    /// even the probe.
    /// </summary>
    [ModuleInitializer]
    internal static void SuppressTheMonitorProbe()
        => Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, "0");
}

/// <summary>
/// Issue #110: the adapter under the runner it is written for. These pass or fail on whether xUnit
/// really invokes the attribute around a test — the assumption a unit test of the mapping cannot
/// make on its own.
/// </summary>
[BobcatFeature("Async daemon"), BobcatScenario]
public class LiveScenarioBracketTests
{
    [Fact]
    public void a_scenario_is_open_and_named_while_the_body_runs()
    {
        var recording = ScenarioRecorder.Current;

        recording.ShouldNotBeNull();

        // {Feature}/{Scenario} — the string that joins run evidence to a slice with no mapping
        // table, built here by the real attribute on a real xUnit test.
        recording.Uid.ShouldBe("Async daemon/a scenario is open and named while the body runs");
    }

    [Fact]
    public void a_step_recorded_in_the_body_lands_on_the_scenario()
    {
        using (ScenarioRecorder.Step("Given", "the events are published"))
        {
        }

        ScenarioRecorder.Current!.Steps.ShouldHaveSingleItem()
            .ToString().ShouldBe("Given the events are published");
    }
}

/// <summary>
/// The same attribute on a class with no <c>[BobcatFeature]</c>, because the fallback naming is
/// what most consumers will actually get.
/// </summary>
[BobcatScenario]
public class UndecoratedFeatureTests
{
    [Fact]
    public void the_class_name_becomes_the_feature()
        => ScenarioRecorder.Current!.Uid
            .ShouldBe("UndecoratedFeatureTests/the class name becomes the feature");
}
