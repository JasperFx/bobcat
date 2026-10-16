using Bobcat.Engine;
using NUnit.Framework;
using Shouldly;

namespace Bobcat.NUnit.Tests;

// bobcat#539: a SpecCatastrophicException halts every scenario that opens after it in the process,
// including one thrown from a [OneTimeSetUp], where the adapter's BeforeTest never runs. Each case
// runs in its own child process, because the halt is process-wide. NUnit runs fixtures and tests in
// name order when nothing is parallel, which is what puts the loss before the scenarios it halts.

[BobcatFeature("Halt after a fixture"), BobcatScenario, NonParallelizable, Explicit("Runs only inside EnvironmentLossTests' child process")]
public class HaltFixtureLoss1_StartsTheApp
{
    [OneTimeSetUp]
    public void StartTheApp()
    {
        if (ProbeRun.Case == "fixture") throw new SpecCatastrophicException("the app did not start");
    }

    [Test]
    public void needs_the_app() => 1.ShouldBe(1);
}

[BobcatFeature("Halt after a fixture"), BobcatScenario, NonParallelizable, Explicit("Runs only inside EnvironmentLossTests' child process")]
public class HaltFixtureLoss2_RunsAfterwards
{
    [Test]
    public void a_later_scenario() => 1.ShouldBe(1);
}

[BobcatFeature("Halt after a scenario"), BobcatScenario, NonParallelizable, Explicit("Runs only inside EnvironmentLossTests' child process")]
public class HaltScenarioLoss1_LosesTheDatabase
{
    [Test]
    public void loses_the_database()
    {
        if (ProbeRun.Case == "scenario") throw new SpecCatastrophicException("the database is gone");
    }
}

[BobcatFeature("Halt after a scenario"), BobcatScenario, NonParallelizable, Explicit("Runs only inside EnvironmentLossTests' child process")]
public class HaltScenarioLoss2_RunsAfterwards
{
    [Test]
    public void a_later_scenario() => 1.ShouldBe(1);
}

public class EnvironmentLossTests
{
    [Test]
    public void a_one_time_setup_that_throws_catastrophically_halts_every_later_scenario()
    {
        var run = ProbeRun.Execute("HaltFixtureLoss", "fixture");

        // The fixture that failed reports the real startup error, on NUnit's own terms.
        run.Console.ShouldContain("the app did not start");
        run.Published.ShouldNotContainKey("needs the app", run.Console);

        run.Published["a later scenario"]
            .ShouldBe("Environment lost: the app did not start (this scenario did not run)", run.Console);
        run.Console.ShouldContain("failed a_later_scenario");
    }

    [Test]
    public void a_scenario_that_throws_catastrophically_halts_every_later_scenario_and_names_itself()
    {
        var run = ProbeRun.Execute("HaltScenarioLoss", "scenario");

        run.Published["loses the database"]
            .ShouldBe("Bobcat.Engine.SpecCatastrophicException: the database is gone", run.Console);
        run.Published["a later scenario"].ShouldBe(
            "Environment lost in Halt after a scenario/loses the database: the database is gone (this scenario did not run)",
            run.Console);
        run.Console.ShouldContain("failed a_later_scenario");
    }

    [Test]
    public void without_a_catastrophic_failure_the_probes_pass()
    {
        var run = ProbeRun.Execute("HaltScenarioLoss", "none");

        run.Published["loses the database"].ShouldBe("CleanPass", run.Console);
        run.Published["a later scenario"].ShouldBe("CleanPass", run.Console);
    }
}
