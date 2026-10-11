using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Bobcat.MSTest.Tests;

/// <summary>
/// Tests that exist to be run by <see cref="ProbeRunTests"/> in a child process, because what they
/// check is what MSTest does with a failing test — and a failing test cannot sit in this suite's own
/// run. In that run they are ignored.
/// </summary>
[TestClass, BobcatFeature("Probes"), BobcatScenario, ProbeOnly]
public class Probes
{
    [TestMethod]
    public void passes() => 1.ShouldBe(1);

    [TestMethod]
    public void throws() => throw new InvalidOperationException("no shard");

    [TestMethod]
    public void gathers_a_wrong_without_throwing()
    {
        // SpecAssert records the wrong on the step in progress and carries on, so the method returns
        // normally. Only the adapter's throw from the cleanup hook can turn this test red.
        using (ScenarioRecorder.Step("Then", "the appointment is confirmed"))
        {
            SpecAssert.Check("status", "Proposed", "Confirmed");
        }
    }

    [TestMethod]
    public void is_inconclusive() => Assert.Inconclusive("no claim made");
}

/// <summary>Runs a test only inside a probe run, and reports it ignored everywhere else.</summary>
public sealed class ProbeOnlyAttribute() : ConditionBaseAttribute(ConditionMode.Include)
{
    public override bool ShouldRun => Environment.GetEnvironmentVariable(ProbeRun.OutputVariable) is not null;

    public override string? IgnoreMessage => "Runs only inside ProbeRunTests' child process";

    public override string GroupName => nameof(ProbeOnlyAttribute);
}

/// <summary>
/// Runs this test assembly again, restricted to the probes, and reads back what Bobcat published for
/// each one as well as what MSTest reported.
/// </summary>
internal sealed class ProbeRun
{
    public const string OutputVariable = "BOBCAT_ADAPTER_PROBE_OUT";

    /// <summary>Inside a probe run, write every published scenario where the parent can read it.</summary>
    [ModuleInitializer]
    internal static void RecordPublishedScenarios()
    {
        if (Environment.GetEnvironmentVariable(OutputVariable) is not { } path) return;

        ScenarioRecorder.ScenarioCompleted += recording =>
        {
            var failure = recording.FailureDescription ?? recording.Failure?.Message ?? recording.GatheredFailures();
            var line = $"{recording.Scenario}\t{(failure ?? "CleanPass").ReplaceLineEndings(" ")}";
            lock (_gate) File.AppendAllLines(path, [line]);
        };
    }

    private static readonly object _gate = new();

    public required Dictionary<string, string> Published { get; init; }
    public required string Console { get; init; }

    public static ProbeRun Execute()
    {
        var output = Path.Combine(Path.GetTempPath(), $"bobcat-mstest-probe-{Guid.NewGuid():N}.txt");
        var assembly = typeof(ProbeRun).Assembly.Location;

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(assembly);
        // The console is asserted on as text, and under GitHub Actions the platform colours it
        // ("\e[31mfailed\e[m throws"), which no literal matches
        start.ArgumentList.Add("--no-ansi");
        start.ArgumentList.Add("--filter");
        start.ArgumentList.Add($"FullyQualifiedName~{typeof(Probes).FullName}.");
        start.Environment[OutputVariable] = output;

        using var process = Process.Start(start)!;
        var console = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit(TimeSpan.FromMinutes(2)).ShouldBeTrue("the probe run did not finish");

        try
        {
            return new ProbeRun
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

/// <summary>
/// Issue #386's open questions, answered against a real MSTest run rather than assumed: is the
/// outcome populated by the time [GlobalTestCleanup] runs, and does a throw from there fail the test?
/// </summary>
[TestClass]
public class ProbeRunTests
{
    private static ProbeRun _run = null!;

    [ClassInitialize]
    public static void RunTheProbes(TestContext _) => _run = ProbeRun.Execute();

    [TestMethod]
    public void a_passing_test_is_published_as_a_pass()
        => _run.Published["passes"].ShouldBe("CleanPass", _run.Console);

    [TestMethod]
    public void a_failing_test_is_published_with_what_it_threw()
        // The outcome and the exception are both read in [GlobalTestCleanup], so this is the
        // proof that MSTest has recorded them by then.
        => _run.Published["throws"].ShouldBe("System.InvalidOperationException: no shard", _run.Console);

    [TestMethod]
    public void a_gathered_wrong_is_published_as_a_failure()
        => _run.Published["gathers a wrong without throwing"].ShouldContain("1 specification step failed", customMessage: _run.Console);

    [TestMethod]
    public void a_gathered_wrong_fails_the_test_too()
    {
        // The half the runner sees: without the throw from the cleanup hook, MSTest would report
        // a red specification as a green test.
        _run.Console.ShouldContain("failed gathers_a_wrong_without_throwing");
        _run.Console.ShouldContain("failed throws");
        _run.Console.ShouldNotContain("failed passes");
    }

    [TestMethod]
    public void an_inconclusive_test_is_withdrawn_rather_than_published()
        => _run.Published.ShouldNotContainKey("is inconclusive", _run.Console);
}
