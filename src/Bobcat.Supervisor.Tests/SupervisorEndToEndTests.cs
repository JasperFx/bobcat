using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// The supervisor driving real worker processes over real MTP server mode. The fake-worker tests
/// prove the scheduling logic; only this proves the protocol, the process handling, and that
/// isolation actually isolates.
/// </summary>
public class SupervisorEndToEndTests : IDisposable
{
    private static readonly string workerPath = locateWorker();
    private readonly List<string> _tempFiles = [];

    private static string locateWorker()
    {
        var configuration = Path.GetFileName(
            Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src") directory = directory.Parent;

        if (directory is null) throw new InvalidOperationException("Could not locate the src directory.");

        return Path.Combine(
            directory.FullName, "Bobcat.Supervisor.SampleWorker", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows()
                ? "Bobcat.Supervisor.SampleWorker.exe"
                : "Bobcat.Supervisor.SampleWorker");
    }

    private MtpWorkerFactory factory(Dictionary<string, string>? environment = null)
    {
        File.Exists(workerPath).ShouldBeTrue($"The sample worker was not built at {workerPath}");
        return new MtpWorkerFactory(workerPath, environment);
    }

    private string tempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bobcat-spike-{Guid.NewGuid():N}.txt");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task discovery_reads_tests_and_their_traits_over_the_wire()
    {
        await using var worker = await MtpWorkerClient.Launch(workerPath);

        var tests = await worker.Discover();

        tests.Select(t => t.Uid).ShouldContain("Fussy/only works alone");

        // Traits arrive at discovery, before anything runs — which is what lets the supervisor
        // plan isolation up front rather than discovering it after a failure.
        var isolated = tests.Single(t => t.Uid == "Fussy/only works alone");
        isolated.Traits[ResilienceTags.Isolated].ShouldBe("true");
        isolated.Traits[ResilienceTags.Retry].ShouldBe("2");
    }

    [Fact]
    public async Task the_client_reports_the_real_pid_of_the_worker_it_drives()
    {
        // Issue #146. The pid is what every external diagnostic — a dump, an RSS sample — has
        // to be pointed at, so it must be the actual OS process, not a synthesized handle.
        await using var worker = await MtpWorkerClient.Launch(workerPath);

        var pid = worker.ProcessId.ShouldNotBeNull();
        pid.ShouldNotBe(Environment.ProcessId);

        using var process = System.Diagnostics.Process.GetProcessById(pid);
        process.HasExited.ShouldBeFalse();

        // The pid also rides on the run's result, which is what lets a fault name its process.
        var result = await worker.Run(["Basics/passes"]);
        result.ProcessId.ShouldBe(pid);
    }

    [Fact]
    public async Task a_filtered_run_executes_only_what_was_asked_for()
    {
        await using var worker = await MtpWorkerClient.Launch(workerPath);

        var result = await worker.Run(["Basics/passes"]);

        result.Outcomes.ShouldHaveSingleItem().Uid.ShouldBe("Basics/passes");
        result.Crashed.ShouldBeFalse();
    }

    [Fact]
    public async Task the_isolation_test_really_does_fail_when_it_shares_a_process()
    {
        // Control. Without this, the headline test below would pass even if isolation did
        // nothing at all.
        await using var worker = await MtpWorkerClient.Launch(workerPath);

        var result = await worker.Run([
            "Basics/passes", "Basics/also passes", "Fussy/only works alone"
        ]);

        var lonely = result.Outcomes.Single(o => o.Uid == "Fussy/only works alone");
        lonely.Succeeded.ShouldBeFalse();
        lonely.ErrorMessage.ShouldContain("not alone");
    }

    [Fact]
    public async Task the_supervisor_gives_an_isolated_test_a_process_of_its_own()
    {
        // The headline: the same test that fails when batched passes under the supervisor,
        // because it was scheduled alone in a fresh process.
        var supervisor = new Supervisor(factory());

        var results = await supervisor.Run();

        var lonely = results.Tests.Single(t => t.Uid == "Fussy/only works alone");
        lonely.Outcome.ShouldBe(RunOutcome.CleanPass);
        lonely.Final.Placement.ShouldBe(AttemptPlacement.IsolatedProcess);
        lonely.AttemptCount.ShouldBe(1); // isolation worked first time — no retry needed

        // And the deliberately broken test is still reported as broken.
        results.Failed.Select(t => t.Uid).ShouldContain("Basics/always fails");
    }

    [Fact]
    public async Task a_flaky_test_passes_on_retry_and_is_reported_as_such()
    {
        var state = tempFile();
        var supervisor = new Supervisor(factory(new Dictionary<string, string>
        {
            ["BOBCAT_FLAKY_STATE"] = state
        }))
        {
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 3 }
        };

        var results = await supervisor.Run();

        var flaky = results.Tests.Single(t => t.Uid == "Fussy/flaky until second attempt");
        flaky.Outcome.ShouldBe(RunOutcome.PassOnRetry);
        flaky.AttemptCount.ShouldBe(2);

        // Never counted as a clean pass.
        results.CleanPasses.ShouldNotContain(t => t.Uid == flaky.Uid);
        results.PassedOnRetry.ShouldContain(t => t.Uid == flaky.Uid);
        results.Summarize().ShouldContain("passed on retry");
    }

    [Fact]
    public async Task a_worker_that_dies_mid_run_leaves_indeterminate_results_not_invented_failures()
    {
        var supervisor = new Supervisor(factory(new Dictionary<string, string>
        {
            ["BOBCAT_CRASH"] = "true"
        }));

        var results = await supervisor.Run();

        // The crashing scenario is in the batch, and everything scheduled after it in that
        // process is lost. Those tests must not be reported as failures the run never saw.
        results.Indeterminate.ShouldNotBeEmpty();
        results.ExitCode.ShouldBe(2);
        results.Indeterminate.ShouldAllBe(t => t.Final.Outcome.State == WorkerTestState.Indeterminate);
    }

    [Fact]
    public async Task a_worker_crash_reports_the_exit_code_rather_than_just_the_closed_socket()
    {
        // The sample worker exits 70 on purpose. Reporting only "the connection closed" would
        // leave the user with an indeterminate run and nothing to act on.
        var supervisor = new Supervisor(factory(new Dictionary<string, string>
        {
            ["BOBCAT_CRASH"] = "true"
        }));

        var results = await supervisor.Run();

        results.WorkerFaults.ShouldNotBeEmpty();
        results.WorkerFaults.ShouldContain(f => f.Contains("exited with code 70"));

        // And it reaches the individual tests that were lost, not just the run summary.
        results.Indeterminate.ShouldAllBe(t => t.Final.Outcome.ErrorMessage!.Contains("code 70"));
        results.Summarize().ShouldContain("code 70");
    }

    [Fact]
    public async Task a_worker_that_dies_with_an_unhandled_exception_reports_its_standard_error()
    {
        // A crash with a real stack trace is the common case, and stderr is the only place the
        // worker gets to explain itself.
        await using var worker = await MtpWorkerClient.Launch(workerPath, new Dictionary<string, string>
        {
            ["BOBCAT_UNHANDLED"] = "true"
        });

        var result = await worker.Run(["Fussy/dies with an unhandled exception when armed"]);

        result.Crashed.ShouldBeTrue();
        result.StandardError.ShouldNotBeNullOrWhiteSpace();
        result.StandardError.ShouldContain("the worker fell over");
        result.Fault.ShouldContain("standard error");
    }

    [Fact]
    public async Task a_resource_that_fails_to_start_is_a_reported_failure_not_a_crash()
    {
        // Issue #123. The worker's broker resource refuses to start, so nothing in it can run.
        // Before the fix the SpecCatastrophicException escaped the MTP host, the process died,
        // and the supervisor could only call every test Indeterminate with a worker fault —
        // which is the right answer for a crash, and the wrong one for a failure the worker knew
        // all about.
        var supervisor = new Supervisor(factory(new Dictionary<string, string>
        {
            ["BOBCAT_START_FAILS"] = "true"
        }));

        var results = await supervisor.Run();

        results.WorkerFaults.ShouldBeEmpty();
        results.Indeterminate.ShouldBeEmpty();

        // Every test has a verdict, and the verdict names the resource.
        results.Tests.Count.ShouldBe(7);
        results.Failed.Count.ShouldBe(7);
        results.Failed.ShouldAllBe(t => t.Final.Outcome.State == WorkerTestState.Error);
        results.Failed.ShouldAllBe(t => t.Final.Outcome.ErrorMessage!.Contains("the broker refused the connection"));

        // An ordinary red build, not "we do not know what happened".
        results.ExitCode.ShouldBe(1);
    }

    [Fact]
    public async Task the_run_reports_how_many_worker_processes_isolation_cost()
    {
        var supervisor = new Supervisor(factory());

        var results = await supervisor.Run();

        // 1 discovery + 1 batch + 1 per isolated test. Surfaced because isolation is not free.
        results.WorkersLaunched.ShouldBe(3);
    }

    [Fact]
    public async Task an_untagged_failure_is_not_retried_even_with_a_budget_available()
    {
        var supervisor = new Supervisor(factory())
        {
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 5 }
        };

        var results = await supervisor.Run();

        results.Tests.Single(t => t.Uid == "Basics/always fails").AttemptCount.ShouldBe(1);
    }

    [Fact]
    public async Task an_isolation_sweep_separates_a_real_interference_victim_from_ordinary_red()
    {
        // The fake-worker tests prove the classification; this proves the isolation was real.
        // "Fussy/only works alone" throws when anything else has executed in its process, so it
        // can only come back green from the sweep if that process genuinely contained one test.
        // A sweep whose isolation did not isolate would report it as failing both ways, which is
        // exactly the false negative worth catching here rather than in a fake.
        var sweep = new IsolationSweep(factory())
        {
            Granularity = SweepGranularity.PerTest,
            MaxParallelWorkers = 2
        };

        var results = await sweep.Run();

        results.Discovered.ShouldBe(7);
        results.Partitions.ShouldBe(7);

        results.InterferenceVictims.Select(f => f.Uid).ShouldBe(["Fussy/only works alone"]);

        // Fails with the suite and alone — a real bug, and the sweep must not dress it up as an
        // isolation problem.
        results.FailedInBoth.Select(f => f.Uid).ShouldBe(["Basics/always fails"]);

        // Nothing here passes only because something else ran first.
        results.OrderDependent.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_per_class_sweep_groups_by_feature_over_the_wire()
    {
        // Two features, so two processes — which only holds because ClassOf understands Bobcat's
        // "Feature: Scenario" display name. Before that fix this returned seven, and the supervisor
        // was quietly partitioning Bobcat suites per test.
        var sweep = new IsolationSweep(factory()) { Granularity = SweepGranularity.PerClass };

        var results = await sweep.Run();

        results.Partitions.ShouldBe(2);
        results.Findings.ShouldAllBe(f => f.Partition == "Basics" || f.Partition == "Fussy");

        // "only works alone" is still found here, and the reason is worth knowing: it is the first
        // scenario registered in Fussy, so when that feature runs as its own group it executes
        // first and nothing has polluted the process yet. Per-class is not blind to this defect —
        // it is blind to one where the *classmate that runs before it* is the culprit, which
        // IsolationSweepTests covers directly.
        results.InterferenceVictims.Select(f => f.Uid).ShouldBe(["Fussy/only works alone"]);
        results.FailedInBoth.Select(f => f.Uid).ShouldBe(["Basics/always fails"]);
    }
}
