using Bobcat.Engine;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Bobcat.MSTest.Tests;

// bobcat#539: a SpecCatastrophicException halts every scenario that opens after it in the process,
// including one thrown from a [ClassInitialize], where the global test hooks never run. Each case
// runs in its own child process, because the halt is process-wide. This assembly is parallel, so
// the probes are [DoNotParallelize]: MSTest then runs them serially in the order the classes are
// declared, which is what puts the loss before the scenario it halts.

[TestClass, BobcatFeature("Halt after a fixture"), BobcatScenario, ProbeOnly, DoNotParallelize]
public class HaltFixtureLoss1_StartsTheApp
{
    [ClassInitialize]
    public static void StartTheApp(TestContext _)
    {
        if (ProbeRun.Case == "fixture") throw new SpecCatastrophicException("the app did not start");
    }

    [TestMethod]
    public void needs_the_app() => 1.ShouldBe(1);
}

[TestClass, BobcatFeature("Halt after a fixture"), BobcatScenario, ProbeOnly, DoNotParallelize]
public class HaltFixtureLoss2_RunsAfterwards
{
    [TestMethod]
    public void a_later_scenario() => 1.ShouldBe(1);
}

[TestClass, BobcatFeature("Halt after a scenario"), BobcatScenario, ProbeOnly, DoNotParallelize]
public class HaltScenarioLoss1_LosesTheDatabase
{
    [TestMethod]
    public void loses_the_database()
    {
        if (ProbeRun.Case == "scenario") throw new SpecCatastrophicException("the database is gone");
    }
}

[TestClass, BobcatFeature("Halt after a scenario"), BobcatScenario, ProbeOnly, DoNotParallelize]
public class HaltScenarioLoss2_RunsAfterwards
{
    [TestMethod]
    public void a_later_scenario() => 1.ShouldBe(1);
}

[TestClass]
public class EnvironmentLossTests
{
    [TestMethod]
    public void a_class_initialize_that_throws_catastrophically_halts_every_later_scenario()
    {
        var run = ProbeRun.Execute("HaltFixtureLoss", "fixture");

        // The class that failed to initialize reports the real startup error, on MSTest's own terms.
        run.Console.ShouldContain("the app did not start");
        run.Published.ShouldNotContainKey("needs the app", run.Console);

        run.Published["a later scenario"]
            .ShouldBe("Environment lost: the app did not start (this scenario did not run)", run.Console);
        run.Console.ShouldContain("failed a_later_scenario");
    }

    [TestMethod]
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

    [TestMethod]
    public void without_a_catastrophic_failure_the_probes_pass()
    {
        var run = ProbeRun.Execute("HaltScenarioLoss", "none");

        run.Published["loses the database"].ShouldBe("CleanPass", run.Console);
        run.Published["a later scenario"].ShouldBe("CleanPass", run.Console);
    }
}
