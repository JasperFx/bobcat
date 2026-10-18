using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Issue #469 against a real worker: <c>Bobcat.Xunit.Samples</c>, a projected xUnit v3 suite,
/// declares recovery hints for its probe failure at assembly, class and method scope, and nothing
/// but its own attributes tells the supervisor about them.
/// </summary>
/// <remarks>
/// The probes are inert unless <c>BOBCAT_HINT_PROBE</c> names a directory, so the samples' own
/// runs are unaffected. Every probe is plain — no trait, no tag — so without the hints the default
/// policy fails each one on its first attempt, which is the control below.
/// </remarks>
public class DeclaredRecoveryHintEndToEndTests : IDisposable
{
    private static readonly string samplesHost = locate("Bobcat.Xunit.Samples");

    private readonly string _state = Path.Combine(Path.GetTempPath(), $"bobcat-hint-probe-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_state)) Directory.Delete(_state, recursive: true);
    }

    private static string locate(string project)
    {
        var configuration = Path.GetFileName(Path.GetDirectoryName(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src") directory = directory.Parent;

        if (directory is null) throw new InvalidOperationException("Could not locate the src directory.");

        return Path.Combine(
            directory.FullName, project, "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? project + ".exe" : project);
    }

    private Supervisor probes(bool readHints)
    {
        File.Exists(samplesHost).ShouldBeTrue($"The projected worker was not built at {samplesHost}");

        return new Supervisor(new MtpWorkerFactory(samplesHost, new Dictionary<string, string>
        {
            ["BOBCAT_MONITOR"] = "0",
            ["BOBCAT_HINT_PROBE"] = _state
        }))
        {
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 2 },
            TestFilter = test => test.DisplayName.Contains("HintProbes."),
            ReadWorkerRecoveryHints = readHints
        };
    }

    private static TestReport probe(SupervisorResults results, string method)
        => results.Tests.Single(t => t.DisplayName.EndsWith(method));

    [Fact]
    public async Task a_workers_declared_hints_decide_how_its_failures_recover()
    {
        var results = await probes(readHints: true).Run();

        results.Tests.Count.ShouldBe(3);

        // Assembly scope: the run-wide default — clears in a fresh process.
        var assembly = probe(results, "recovers_in_a_fresh_process_by_the_assembly_default");
        assembly.Outcome.ShouldBe(RunOutcome.PassOnRetry);
        assembly.Attempts[0].Disposition.Kind.ShouldBe(DispositionKind.RetryInFreshProcess);
        assembly.Attempts[0].Disposition.Hint!.Scope.ShouldBe(HintScope.Global);
        assembly.Attempts[1].Placement.ShouldBe(AttemptPlacement.IsolatedProcess);

        // Class scope overrides it: clears on a retry, in the same process.
        var byClass = probe(results, "recovers_on_a_retry_by_the_class_override");
        byClass.Outcome.ShouldBe(RunOutcome.PassOnRetry);
        byClass.Attempts[0].Disposition.Kind.ShouldBe(DispositionKind.RetryInProcess);
        byClass.Attempts[0].Disposition.Hint!.Source.ShouldBe("ClassHintProbes");

        // Method scope overrides the class: never recovers, so it is not retried at all.
        var byMethod = probe(results, "never_recovers_by_the_method_override");
        byMethod.AttemptCount.ShouldBe(1);
        byMethod.Attempts[0].Disposition.Kind.ShouldBe(DispositionKind.FailAndContinue);
        byMethod.Attempts[0].Disposition.Hint!.Scope.ShouldBe(HintScope.Test);
        byMethod.Attempts[0].Disposition.Reason.ShouldContain("the method-level override");
    }

    [Fact]
    public async Task without_the_workers_hints_the_same_failures_are_not_retried()
    {
        // The control: identical worker, identical failures, the manifest left unread. Nothing is
        // tagged, so the default policy fails every probe on its first attempt.
        var results = await probes(readHints: false).Run();

        results.Tests.Count.ShouldBe(3);
        results.Tests.ShouldAllBe(t => t.AttemptCount == 1 && !t.Final.Succeeded);
    }
}
