using System.Runtime.CompilerServices;
using Bobcat.Engine;

namespace Bobcat.TUnit.Tests.Halt;

// bobcat#539, driven from Bobcat.Supervisor.Tests/TUnitHaltTests as a child process with
// BOBCAT_HALT_PROBE naming the case: a SpecCatastrophicException halts every scenario that opens
// after it in the process, including one thrown from a [Before(Class)] hook. [Explicit] keeps the
// probes out of an ordinary run; [DependsOn(..) { ProceedOnFailure = true }] is what puts the loss
// before the scenario it halts, since TUnit runs tests in parallel.

[BobcatFeature("Halt after a fixture"), BobcatScenario, Explicit]
public class FixtureLoss
{
    [Before(Class)]
    public static Task StartTheApp()
        => HaltProbe.Case == "fixture"
            ? throw new SpecCatastrophicException("the app did not start")
            : Task.CompletedTask;

    [Test]
    public Task needs_the_app() => Task.CompletedTask;
}

[BobcatFeature("Halt after a fixture"), BobcatScenario, Explicit]
public class AfterFixtureLoss
{
    [Test, DependsOn(typeof(FixtureLoss), ProceedOnFailure = true)]
    public Task a_later_scenario() => Task.CompletedTask;
}

[BobcatFeature("Halt after a scenario"), BobcatScenario, Explicit]
public class ScenarioLoss
{
    [Test]
    public Task loses_the_database()
        => HaltProbe.Case == "scenario"
            ? throw new SpecCatastrophicException("the database is gone")
            : Task.CompletedTask;
}

[BobcatFeature("Halt after a scenario"), BobcatScenario, Explicit]
public class AfterScenarioLoss
{
    [Test, DependsOn(typeof(ScenarioLoss), ProceedOnFailure = true)]
    public Task a_later_scenario() => Task.CompletedTask;
}

internal static class HaltProbe
{
    public static string? Case => Environment.GetEnvironmentVariable("BOBCAT_HALT_PROBE");

    private static readonly Lock _gate = new();

    /// <summary>Inside a probe run, write every published scenario where the parent can read it.</summary>
    [ModuleInitializer]
    internal static void RecordPublishedScenarios()
    {
        if (Environment.GetEnvironmentVariable("BOBCAT_HALT_PROBE_OUT") is not { } path) return;

        ScenarioRecorder.ScenarioCompleted += recording =>
        {
            var failure = recording.FailureDescription ?? recording.Failure?.Message ?? recording.GatheredFailures();
            var line = $"{recording.Uid}\t{(failure ?? "CleanPass").ReplaceLineEndings(" ")}";
            lock (_gate) File.AppendAllLines(path, [line]);
        };
    }
}
