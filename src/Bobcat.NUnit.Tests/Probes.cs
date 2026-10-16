using System.Diagnostics;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;

namespace Bobcat.NUnit.Tests;

/// <summary>
/// Tests that exist to be run by <see cref="ProbeRunTests"/> in a child process, because what they
/// check is what NUnit does with a failing test — and a failing test cannot sit in this suite's own
/// run. <c>[Explicit]</c> keeps them out of it: NUnit runs an explicit test only when a filter names it.
/// </summary>
[BobcatFeature("Probes"), BobcatScenario, Explicit("Runs only inside ProbeRunTests' child process")]
public class Probes
{
    [Test]
    public void passes() => 1.ShouldBe(1);

    [Test]
    public void throws() => throw new InvalidOperationException("no shard");

    [Test]
    public void gathers_a_wrong_without_throwing()
    {
        // SpecAssert records the wrong on the step in progress and carries on, so the method returns
        // normally. Only the adapter's throw from AfterTest can turn this test red.
        using (ScenarioRecorder.Step("Then", "the appointment is confirmed"))
        {
            SpecAssert.Check("status", "Proposed", "Confirmed");
        }
    }

    [Test]
    public void is_inconclusive() => Assert.Inconclusive("no claim made");
}

/// <summary>
/// bobcat#545's probes: Acceptance specs, failing three ways and passing one, run by
/// <see cref="AcceptanceProbeRunTests"/> in a child process of their own.
/// </summary>
[BobcatFeature("Acceptance probes"), BobcatScenario(Lifecycle = Lifecycle.Acceptance),
 Explicit("Runs only inside AcceptanceProbeRunTests' child process")]
public class AcceptanceProbes
{
    [Test]
    public void passes() => 1.ShouldBe(1);

    [Test]
    public void throws() => throw new NotImplementedException("not built yet");

    [Test]
    public void fails_an_nunit_assertion() => Assert.That(1, Is.EqualTo(2));

    [Test]
    public void gathers_a_wrong_without_throwing()
    {
        using (ScenarioRecorder.Step("Then", "the appointment is confirmed"))
        {
            SpecAssert.Check("status", "Proposed", "Confirmed");
        }
    }
}

/// <summary>
/// Runs this test assembly again, restricted to the probes, and reads back what Bobcat published for
/// each one as well as what NUnit reported.
/// </summary>
internal sealed class ProbeRun
{
    public const string OutputVariable = "BOBCAT_ADAPTER_PROBE_OUT";

    /// <summary>Which bobcat#539 halt case a probe run acts out; unset everywhere else.</summary>
    public const string CaseVariable = "BOBCAT_HALT_PROBE";

    public static string? Case => Environment.GetEnvironmentVariable(CaseVariable);

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

    /// <param name="classPrefix">The probe classes to run, by the start of their name; <see cref="Probes"/> by default.</param>
    /// <param name="haltCase">The <see cref="CaseVariable"/> value for the run, if any.</param>
    /// <param name="strict">Run under BOBCAT_LIFECYCLE=strict (bobcat#545).</param>
    public static ProbeRun Execute(string? classPrefix = null, string? haltCase = null, bool strict = false)
    {
        var output = Path.Combine(Path.GetTempPath(), $"bobcat-nunit-probe-{Guid.NewGuid():N}.txt");
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
        start.ArgumentList.Add($"FullyQualifiedName~{typeof(Probes).Namespace}.{classPrefix ?? nameof(Probes) + "."}");
        start.Environment[OutputVariable] = output;
        if (haltCase is not null) start.Environment[CaseVariable] = haltCase;
        start.Environment[SpecLifecycle.EnvironmentVariable] = strict ? SpecLifecycle.StrictValue : "";

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
/// Issue #385's open questions, answered against a real NUnit run rather than assumed: is the outcome
/// populated by the time AfterTest runs, and does a throw from there fail the test?
/// </summary>
public class ProbeRunTests
{
    private static ProbeRun _run = null!;

    [OneTimeSetUp]
    public static void RunTheProbes() => _run = ProbeRun.Execute();

    [Test]
    public void a_passing_test_is_published_as_a_pass()
        => _run.Published["passes"].ShouldBe("CleanPass", _run.Console);

    [Test]
    public void a_failing_test_is_published_with_what_it_threw()
        // The outcome and its message are both read in AfterTest, so this is the proof that NUnit
        // has recorded them by then.
        => _run.Published["throws"].ShouldBe("System.InvalidOperationException: no shard", _run.Console);

    [Test]
    public void a_gathered_wrong_is_published_as_a_failure()
        => _run.Published["gathers a wrong without throwing"].ShouldContain("1 specification step failed", customMessage: _run.Console);

    [Test]
    public void a_gathered_wrong_fails_the_test_too()
    {
        // The half the runner sees: without the throw from AfterTest, NUnit would report a red
        // specification as a green test.
        _run.Console.ShouldContain("failed gathers_a_wrong_without_throwing");
        _run.Console.ShouldContain("failed throws");
        _run.Console.ShouldNotContain("failed passes");
    }

    [Test]
    public void an_inconclusive_test_is_withdrawn_rather_than_published()
        => _run.Published.ShouldNotContainKey("is inconclusive", _run.Console);
}

/// <summary>
/// bobcat#545 against a real NUnit run: a failing Acceptance spec is reported ignored with its reason
/// however it failed — a throw, NUnit's own assertion, a gathered wrong — while Bobcat still publishes
/// the failure; strict mode leaves all three failed.
/// </summary>
public class AcceptanceProbeRunTests
{
    private static ProbeRun _run = null!;
    private static ProbeRun _strict = null!;

    [OneTimeSetUp]
    public static void RunTheProbes()
    {
        _run = ProbeRun.Execute(nameof(AcceptanceProbes) + ".", strict: false);
        _strict = ProbeRun.Execute(nameof(AcceptanceProbes) + ".", strict: true);
    }

    [Test]
    public void every_failing_acceptance_spec_is_ignored_with_its_reason_and_none_fails()
    {
        _run.Console.ShouldNotContain("failed ", customMessage: _run.Console);
        _run.Console.ShouldContain("skipped throws", customMessage: _run.Console);
        _run.Console.ShouldContain("skipped fails_an_nunit_assertion", customMessage: _run.Console);
        _run.Console.ShouldContain("skipped gathers_a_wrong_without_throwing", customMessage: _run.Console);
        _run.Console.ShouldContain(
            "acceptance spec, not yet passing: System.NotImplementedException: not built yet", customMessage: _run.Console);
    }

    [Test]
    public void bobcat_still_publishes_the_failure()
    {
        _run.Published["throws"].ShouldBe("System.NotImplementedException: not built yet", _run.Console);
        _run.Published["gathers a wrong without throwing"].ShouldContain("1 specification step failed", customMessage: _run.Console);
        _run.Published["passes"].ShouldBe("CleanPass", _run.Console);
    }

    [Test]
    public void strict_mode_leaves_them_failed()
    {
        _strict.Console.ShouldContain("failed throws", customMessage: _strict.Console);
        _strict.Console.ShouldContain("failed fails_an_nunit_assertion", customMessage: _strict.Console);
        _strict.Console.ShouldContain("failed gathers_a_wrong_without_throwing", customMessage: _strict.Console);
        _strict.Console.ShouldNotContain(SpecLifecycle.SkipReasonPrefix, customMessage: _strict.Console);
    }
}
