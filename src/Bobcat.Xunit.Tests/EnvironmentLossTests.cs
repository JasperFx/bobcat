using System.Diagnostics;
using System.Runtime.CompilerServices;
using Bobcat.Engine;
using Shouldly;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace Bobcat.Xunit.Tests;

// bobcat#539: a SpecCatastrophicException halts every scenario that opens after it in the process,
// including one thrown from setup, where Bobcat's before/after bracket never runs. The probes below
// are run in a child process, one case per process, because the halt is process-wide by design and
// would otherwise stop this suite's own run. Each case is one collection, ordered by class
// name, so the environment is lost before the scenarios it halts.

/// <summary>A class fixture standing in for an application that will not start.</summary>
public sealed class AppThatWillNotStart
{
    public AppThatWillNotStart()
    {
        if (HaltProbe.Case == "fixture") throw new SpecCatastrophicException("the app did not start");
    }
}

[CollectionDefinition(Name)]
[TestClassOrderer(typeof(ByName))]
public sealed class FixtureLossCollection
{
    public const string Name = "bobcat#539 fixture loss";
}

[Collection(FixtureLossCollection.Name), BobcatFeature("Halt after a fixture"), BobcatScenario]
public class FixtureLoss1_StartsTheApp(AppThatWillNotStart app) : IClassFixture<AppThatWillNotStart>
{
    [Fact(Explicit = true)]
    public void needs_the_app() => app.ShouldNotBeNull();
}

[Collection(FixtureLossCollection.Name), BobcatFeature("Halt after a fixture"), BobcatScenario]
public class FixtureLoss2_RunsAfterwards
{
    [Fact(Explicit = true)]
    public void a_later_scenario() => 1.ShouldBe(1);

    [Fact(Explicit = true)]
    public void another_later_scenario() => 1.ShouldBe(1);
}

[CollectionDefinition(Name)]
[TestClassOrderer(typeof(ByName))]
public sealed class LifetimeLossCollection
{
    public const string Name = "bobcat#539 lifetime loss";
}

[Collection(LifetimeLossCollection.Name), BobcatFeature("Halt after IAsyncLifetime"), BobcatScenario]
public class LifetimeLoss1_StartsTheHost : IAsyncLifetime
{
    public ValueTask InitializeAsync()
        => HaltProbe.Case == "lifetime"
            ? throw new SpecCatastrophicException("the host did not start")
            : ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact(Explicit = true)]
    public void needs_the_host() => 1.ShouldBe(1);
}

[Collection(LifetimeLossCollection.Name), BobcatFeature("Halt after IAsyncLifetime"), BobcatScenario]
public class LifetimeLoss2_RunsAfterwards
{
    [Fact(Explicit = true)]
    public void a_later_scenario() => 1.ShouldBe(1);
}

[CollectionDefinition(Name)]
[TestClassOrderer(typeof(ByName))]
public sealed class ScenarioLossCollection
{
    public const string Name = "bobcat#539 scenario loss";
}

[Collection(ScenarioLossCollection.Name), BobcatFeature("Halt after a scenario"), BobcatScenario]
public class ScenarioLoss1_LosesTheDatabase
{
    [Fact(Explicit = true)]
    public void loses_the_database()
    {
        if (HaltProbe.Case == "scenario") throw new SpecCatastrophicException("the database is gone");
    }
}

[Collection(ScenarioLossCollection.Name), BobcatFeature("Halt after a scenario"), BobcatScenario]
public class ScenarioLoss2_RunsAfterwards
{
    [Fact(Explicit = true)]
    public void a_later_scenario() => 1.ShouldBe(1);
}

[CollectionDefinition(Name)]
[TestClassOrderer(typeof(ByName))]
public sealed class AcceptanceLossCollection
{
    public const string Name = "bobcat#539 + #545 acceptance loss";
}

/// <summary>
/// An Acceptance spec (bobcat#545) that loses the environment: a lost environment is never softened
/// into a skip, neither on the spec that lost it nor on a halted Acceptance spec after it.
/// </summary>
[Collection(AcceptanceLossCollection.Name), BobcatFeature("Halt in acceptance")]
public class AcceptanceLoss1_LosesTheDatabase
{
    [BobcatSpec(Lifecycle = Lifecycle.Acceptance, Explicit = true)]
    public void loses_the_database()
    {
        if (HaltProbe.Case == "acceptance") throw new SpecCatastrophicException("the database is gone");
        throw new NotImplementedException("not built yet");
    }
}

[Collection(AcceptanceLossCollection.Name), BobcatFeature("Halt in acceptance")]
public class AcceptanceLoss2_RunsAfterwards
{
    [BobcatSpec(Lifecycle = Lifecycle.Acceptance, Explicit = true)]
    public void a_later_scenario() => throw new NotImplementedException("not built yet");
}

/// <summary>Orders a probe collection by class name.</summary>
public sealed class ByName : ITestClassOrderer
{
    IReadOnlyCollection<TTestClass?> ITestClassOrderer.OrderTestClasses<TTestClass>(
        IReadOnlyCollection<TTestClass?> testClasses) where TTestClass : default
        => testClasses.OrderBy(x => x?.TestClassName, StringComparer.Ordinal).ToArray();
}

/// <summary>
/// Runs this test assembly again, restricted to one probe case, and reads back what Bobcat published
/// for each scenario as well as what xUnit reported.
/// </summary>
internal sealed class HaltProbe
{
    public const string CaseVariable = "BOBCAT_HALT_PROBE";
    public const string OutputVariable = "BOBCAT_HALT_PROBE_OUT";

    public static string? Case => Environment.GetEnvironmentVariable(CaseVariable);

    [ModuleInitializer]
    internal static void RecordPublishedScenarios()
    {
        if (Environment.GetEnvironmentVariable(OutputVariable) is not { } path) return;

        ScenarioRecorder.ScenarioCompleted += recording =>
        {
            var failure = recording.FailureDescription ?? recording.Failure?.Message ?? recording.GatheredFailures();
            var acceptance = recording.AcceptanceFailing ? "[acceptance: failing] " : "";
            var line = $"{recording.Scenario}\t{acceptance}{(failure ?? "CleanPass").ReplaceLineEndings(" ")}";
            lock (_gate) File.AppendAllLines(path, [line]);
        };
    }

    private static readonly object _gate = new();

    public required Dictionary<string, string> Published { get; init; }
    public required string Console { get; init; }

    public static HaltProbe Execute(string probeCase, string classPrefix)
    {
        var output = Path.Combine(Path.GetTempPath(), $"bobcat-xunit-halt-{Guid.NewGuid():N}.txt");

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(typeof(HaltProbe).Assembly.Location);
        start.ArgumentList.Add("--no-ansi");
        start.ArgumentList.Add("--explicit");
        start.ArgumentList.Add("only");
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add("detailed");
        start.ArgumentList.Add("--filter-class");
        start.ArgumentList.Add($"{typeof(HaltProbe).Namespace}.{classPrefix}*");
        start.Environment[CaseVariable] = probeCase;
        start.Environment[OutputVariable] = output;

        using var process = Process.Start(start)!;
        var console = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit(TimeSpan.FromMinutes(2)).ShouldBeTrue("the probe run did not finish");

        try
        {
            return new HaltProbe
            {
                Published = File.Exists(output)
                    ? File.ReadAllLines(output).Select(x => x.Split('\t', 2)).ToDictionary(x => x[0], x => x[1])
                    : [],
                Console = console.Result + errors.Result
            };
        }
        finally
        {
            File.Delete(output);
        }
    }
}

public class EnvironmentLossTests
{
    [Fact]
    public void a_class_fixture_that_throws_catastrophically_halts_every_later_scenario()
    {
        var run = HaltProbe.Execute("fixture", "FixtureLoss");

        // The class whose fixture failed reports the real startup error: xUnit fails its tests with
        // it, and Bobcat's bracket never ran for them.
        run.Console.ShouldContain("the app did not start");
        run.Published.ShouldNotContainKey("needs the app", run.Console);

        // Everything after it fails without running, naming the startup failure.
        run.Published["a later scenario"]
            .ShouldBe("Environment lost: the app did not start (this scenario did not run)", run.Console);
        run.Published["another later scenario"].ShouldStartWith("Environment lost: the app did not start");
        run.Console.ShouldContain($"failed {typeof(FixtureLoss2_RunsAfterwards).FullName}.a_later_scenario");
        run.Console.ShouldContain($"failed {typeof(FixtureLoss2_RunsAfterwards).FullName}.another_later_scenario");
    }

    [Fact]
    public void an_async_lifetime_that_throws_catastrophically_halts_every_later_scenario()
    {
        var run = HaltProbe.Execute("lifetime", "LifetimeLoss");

        run.Console.ShouldContain("the host did not start");
        run.Published["a later scenario"]
            .ShouldBe("Environment lost: the host did not start (this scenario did not run)", run.Console);
        run.Console.ShouldContain($"failed {typeof(LifetimeLoss2_RunsAfterwards).FullName}.a_later_scenario");
    }

    [Fact]
    public void a_scenario_that_throws_catastrophically_halts_every_later_scenario_and_names_itself()
    {
        var run = HaltProbe.Execute("scenario", "ScenarioLoss");

        // The scenario that met it reports the real failure.
        run.Published["loses the database"]
            .ShouldBe("Bobcat.Engine.SpecCatastrophicException: the database is gone", run.Console);

        run.Published["a later scenario"].ShouldBe(
            "Environment lost in Halt after a scenario/loses the database: the database is gone (this scenario did not run)",
            run.Console);
        run.Console.ShouldContain($"failed {typeof(ScenarioLoss2_RunsAfterwards).FullName}.a_later_scenario");
    }

    [Fact]
    public void a_lost_environment_is_never_softened_into_an_acceptance_skip()
    {
        // bobcat#545 meets #539: the Acceptance spec that lost the environment is the cause every
        // later scenario names, so it fails rather than reading as a skip; and a halted Acceptance
        // spec fails before its body, where no adapter can report a skip, so Bobcat says failed too.
        var run = HaltProbe.Execute("acceptance", "AcceptanceLoss");

        run.Published["loses the database"]
            .ShouldBe("Bobcat.Engine.SpecCatastrophicException: the database is gone", run.Console);
        run.Console.ShouldContain($"failed {typeof(AcceptanceLoss1_LosesTheDatabase).FullName}.loses_the_database");

        run.Published["a later scenario"].ShouldBe(
            "Environment lost in Halt in acceptance/loses the database: the database is gone (this scenario did not run)",
            run.Console);
        run.Console.ShouldContain($"failed {typeof(AcceptanceLoss2_RunsAfterwards).FullName}.a_later_scenario");
        run.Console.ShouldNotContain(SpecLifecycle.SkipReasonPrefix);
    }

    [Fact]
    public void without_a_lost_environment_the_same_acceptance_specs_are_skipped()
    {
        // The control: the same two specs failing ordinarily are acceptance: failing and skipped.
        var run = HaltProbe.Execute("none", "AcceptanceLoss");

        run.Published["loses the database"].ShouldStartWith("[acceptance: failing] ", customMessage: run.Console);
        run.Published["a later scenario"].ShouldStartWith("[acceptance: failing] ", customMessage: run.Console);
        run.Console.ShouldContain($"skipped {typeof(AcceptanceLoss1_LosesTheDatabase).FullName}.loses_the_database");
        run.Console.ShouldContain($"skipped {typeof(AcceptanceLoss2_RunsAfterwards).FullName}.a_later_scenario");
    }

    [Fact]
    public void without_a_catastrophic_failure_the_probes_pass()
    {
        // The control: the same probes with nothing thrown, so the halts above are the throw's doing.
        var run = HaltProbe.Execute("none", "ScenarioLoss");

        run.Published["loses the database"].ShouldBe("CleanPass", run.Console);
        run.Published["a later scenario"].ShouldBe("CleanPass", run.Console);
    }
}
