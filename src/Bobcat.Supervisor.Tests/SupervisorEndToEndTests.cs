using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// The supervisor driving real worker processes over real MTP server mode. The fake-worker tests
/// prove the scheduling logic; only this proves the protocol, the process handling, and that
/// isolation actually isolates.
/// </summary>
/// <remarks>
/// The worker is <c>Bobcat.Supervisor.ProjectedWorker</c>, an ordinary projected xUnit v3 suite
/// (bobcat#471 moved these off the Gherkin-lane <c>Bobcat.Supervisor.SampleWorker</c>). Its uids
/// are xUnit's opaque hashes, so tests are named by display name — see
/// <see cref="WorkerHosts.IsNamed"/> — and run by the uid discovery handed back.
/// </remarks>
public class SupervisorEndToEndTests : IDisposable
{
    private static readonly string workerPath = WorkerHosts.ProjectedWorker;
    private readonly List<string> _tempFiles = [];

    private MtpWorkerFactory factory(Dictionary<string, string>? environment = null)
    {
        File.Exists(workerPath).ShouldBeTrue($"The projected worker was not built at {workerPath}");
        return new MtpWorkerFactory(workerPath, environment);
    }

    private static TestReport named(SupervisorResults results, string classAndMethod)
        => results.Tests.Single(t => WorkerHosts.IsNamed(t.DisplayName, classAndMethod));

    private static bool isNamed(TestReport test, string classAndMethod)
        => WorkerHosts.IsNamed(test.DisplayName, classAndMethod);

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

        // Traits arrive at discovery, before anything runs — which is what lets the supervisor
        // plan isolation up front rather than discovering it after a failure. They are plain xUnit
        // [Trait]s here: the supervisor reads traits, whatever framework wrote them.
        var isolated = tests.Single(t => WorkerHosts.IsNamed(t.DisplayName, "Fussy.only_works_alone"));
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
        var result = await worker.Run(await WorkerHosts.UidsOf(worker, "Basics.passes"));
        result.ProcessId.ShouldBe(pid);
    }

    [Fact]
    public async Task the_client_samples_the_real_workers_resident_set()
    {
        // Issue #149. Process.WorkingSet64 on the handle already held — and null once the
        // process is gone, because unmeasured must never read as zero.
        var worker = await MtpWorkerClient.Launch(workerPath);

        var bytes = worker.SampleWorkingSet().ShouldNotBeNull();
        bytes.ShouldBeGreaterThan(0);

        await worker.DisposeAsync();
        worker.SampleWorkingSet().ShouldBeNull();
    }

    [Fact]
    public async Task a_filtered_run_executes_only_what_was_asked_for()
    {
        await using var worker = await MtpWorkerClient.Launch(workerPath);

        var passes = await WorkerHosts.UidsOf(worker, "Basics.passes");
        var result = await worker.Run(passes);

        result.Outcomes.ShouldHaveSingleItem().Uid.ShouldBe(passes.Single());
        result.Crashed.ShouldBeFalse();
    }

    [Fact]
    public async Task the_isolation_test_really_does_fail_when_it_shares_a_process()
    {
        // Control. Without this, the headline test below would pass even if isolation did
        // nothing at all.
        await using var worker = await MtpWorkerClient.Launch(workerPath);

        var uids = await WorkerHosts.UidsOf(
            worker, "Basics.passes", "Basics.also_passes", "Fussy.only_works_alone");
        var result = await worker.Run(uids);

        var lonely = result.Outcomes.Single(o => o.Uid == uids[2]);
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

        var lonely = named(results, "Fussy.only_works_alone");
        lonely.Outcome.ShouldBe(RunOutcome.CleanPass);
        lonely.Final.Placement.ShouldBe(AttemptPlacement.IsolatedProcess);
        lonely.AttemptCount.ShouldBe(1); // isolation worked first time — no retry needed

        // And the deliberately broken test is still reported as broken.
        results.Failed.ShouldContain(t => isNamed(t, "Basics.always_fails"));
    }

    private sealed class RecordingSink : Bobcat.Monitoring.IMonitorEventSink
    {
        private readonly List<Bobcat.Monitoring.MonitorEvent> _events = [];

        public void Post(Bobcat.Monitoring.MonitorEvent @event)
        {
            lock (_events) _events.Add(@event);
        }

        public IReadOnlyList<Bobcat.Monitoring.MonitorEvent> Events
        {
            get { lock (_events) return _events.ToArray(); }
        }
    }

    [Fact]
    public async Task a_real_workers_failure_reaches_the_wire_with_the_reason_its_node_carried()
    {
        // bobcat#825, against a real MTP host rather than the fake: the terminal node's
        // error.message and error.stacktrace are what readNode reads, and this is the proof those
        // keys are what xUnit v3 on MTP 2.5 actually sends — a fake agreeing with itself is not.
        var sink = new RecordingSink();
        var supervisor = new Supervisor(factory()) { PublishToMonitor = true, MonitorSink = sink };

        await supervisor.Run();

        var finished = sink.Events.OfType<Bobcat.Monitoring.TestFinished>().ToList();
        var broken = finished.Single(e => WorkerHosts.IsNamed(e.DisplayName, "Basics.always_fails"));

        // xUnit v3 calls a thrown exception "Error" and a disagreeing assertion "Failed"; the
        // framework's word travels verbatim, and either one needs its reason.
        broken.State.ShouldBeOneOf("Failed", "Error");
        broken.ErrorMessage.ShouldNotBeNull().ShouldContain("this one never works");
        broken.StackTrace.ShouldNotBeNull().ShouldContain("always_fails");

        // Nothing non-passing left the wire without a reason, and nothing passing carried one.
        finished.Where(e => e.State is not ("Passed" or "Skipped")).ShouldAllBe(e => e.ErrorMessage != null);
        finished.Where(e => e.State is "Passed" or "Skipped").ShouldAllBe(e => e.ErrorMessage == null && e.StackTrace == null);
    }

    [Fact]
    public async Task a_worker_reads_its_connection_strings_through_configuration_as_an_aspire_app_would()
    {
        // Issue #414: the same IConfiguration.GetConnectionString call an application makes under an
        // Aspire AppHost, run inside a real worker process the supervisor launched
        var probe = tempFile();
        var supervisor = new Supervisor(new MtpWorkerFactory(workerPath, new Dictionary<string, string>
        {
            ["BOBCAT_CONNECTION_PROBE"] = probe
        })
        {
            // #region worker-connection-strings
            ConnectionStrings = new WorkerConnectionStrings()
                .Add("postgres", worker => $"Host=localhost;Database=specs_w{worker.Lane}")
                .Add("rabbitmq", "amqp://guest:guest@localhost:5672")
            // #endregion worker-connection-strings
        })
        {
            MaxParallelWorkers = 1
        };

        await supervisor.Run();

        var lines = await File.ReadAllLinesAsync(probe);
        lines.ShouldContain("postgres=Host=localhost;Database=specs_w0");
        lines.ShouldContain("rabbitmq=amqp://guest:guest@localhost:5672");
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

        var flaky = named(results, "Fussy.flaky_until_second_attempt");
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
        // The projected worker exits 70 on purpose. Reporting only "the connection closed" would
        // leave the user with an indeterminate run and nothing to act on.
        var supervisor = new Supervisor(factory(new Dictionary<string, string>
        {
            ["BOBCAT_CRASH"] = "true"
        }));

        var results = await supervisor.Run();

        results.WorkerFaults.ShouldNotBeEmpty();
        results.WorkerFaults.ShouldContain(f => f.Contains("exited with code 70"));

        // And it reaches the individual tests that were lost, not just the run summary.
        // Null-checked rather than `!`-suppressed: an outcome reaching here unexplained is the
        // defect this asserts against, and it should read as a failed assertion rather than as a
        // NullReferenceException inside Shouldly's predicate.
        results.Indeterminate.ShouldAllBe(t =>
            t.Final.Outcome.ErrorMessage != null && t.Final.Outcome.ErrorMessage.Contains("code 70"));
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

        var result = await worker.Run(
            await WorkerHosts.UidsOf(worker, "Fussy.dies_with_an_unhandled_exception_when_armed"));

        result.Crashed.ShouldBeTrue();
        result.StandardError.ShouldNotBeNullOrWhiteSpace();
        result.StandardError.ShouldContain("the worker fell over");
        result.Fault.ShouldContain("standard error");
    }

    [Fact]
    public async Task a_resource_that_fails_to_start_is_a_reported_failure_not_a_crash()
    {
        // Issue #123's shape in a projected suite: the worker's broker, an xUnit assembly fixture,
        // refuses to start, so nothing in it can run. A supervisor that called every test
        // Indeterminate with a worker fault would be giving the right answer for a crash and the
        // wrong one for a failure the worker knew all about.
        var supervisor = new Supervisor(factory(new Dictionary<string, string>
        {
            ["BOBCAT_START_FAILS"] = "true"
        }));

        var results = await supervisor.Run();

        results.WorkerFaults.ShouldBeEmpty();
        results.Indeterminate.ShouldBeEmpty();

        // Every test has a verdict, and the verdict names the resource.
        results.Tests.Count.ShouldBe(9);
        results.Failed.Count.ShouldBe(9);
        results.Failed.ShouldAllBe(t =>
            t.Final.Outcome.ErrorMessage != null
            && t.Final.Outcome.ErrorMessage.Contains("the broker refused the connection"));

        // An ordinary red build, not "we do not know what happened".
        results.ExitCode.ShouldBe(1);
    }

    [Fact]
    public async Task a_workers_catastrophic_failure_aborts_the_run_and_is_never_retried()
    {
        // Issue #538. Out of process there is no SpecCatastrophicException to catch — only its
        // name in the message xUnit reports — and the probe carries a Retry trait with a budget
        // available, so before the fix it was retried and the run carried on as if nothing happened.
        var supervisor = new Supervisor(factory(new Dictionary<string, string>
        {
            ["BOBCAT_CATASTROPHIC"] = "true"
        }))
        {
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 3 }
        };

        var results = await supervisor.Run();

        var doomed = named(results, "Fussy.loses_the_environment_when_armed");
        doomed.Final.Outcome.ErrorType.ShouldBe("Bobcat.Engine.SpecCatastrophicException");
        doomed.AttemptCount.ShouldBe(1);
        doomed.Final.Disposition.Kind.ShouldBe(DispositionKind.AbortRun);

        results.AbortReason.ShouldNotBeNull();
        results.AbortReason.ShouldContain("the application could not start");
        results.ExitCode.ShouldBe(2);
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

        named(results, "Basics.always_fails").AttemptCount.ShouldBe(1);
    }

    [Fact]
    public async Task an_isolation_sweep_separates_a_real_interference_victim_from_ordinary_red()
    {
        // The fake-worker tests prove the classification; this proves the isolation was real.
        // "Fussy.only_works_alone" throws when anything else has executed in its process, so it
        // can only come back green from the sweep if that process genuinely contained one test.
        // A sweep whose isolation did not isolate would report it as failing both ways, which is
        // exactly the false negative worth catching here rather than in a fake.
        var sweep = new IsolationSweep(factory())
        {
            Granularity = SweepGranularity.PerTest,
            MaxParallelWorkers = 2
        };

        var results = await sweep.Run();

        results.Discovered.ShouldBe(9);
        results.Partitions.ShouldBe(9);

        results.InterferenceVictims.ShouldHaveSingleItem().DisplayName.ShouldEndWith(".Fussy.only_works_alone");

        // Fails with the suite and alone — a real bug, and the sweep must not dress it up as an
        // isolation problem.
        results.FailedInBoth.ShouldHaveSingleItem().DisplayName.ShouldEndWith(".Basics.always_fails");

        // Nothing here passes only because something else ran first.
        results.OrderDependent.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_per_class_sweep_groups_by_feature_over_the_wire()
    {
        // Two classes, so two processes — ClassOf reading the class off xUnit's fully qualified
        // "Namespace.Class.method" display name.
        var sweep = new IsolationSweep(factory()) { Granularity = SweepGranularity.PerClass };

        var results = await sweep.Run();

        results.Partitions.ShouldBe(2);
        results.Findings.ShouldAllBe(f => f.Partition.EndsWith(".Basics") || f.Partition.EndsWith(".Fussy"));

        // "only works alone" is still found here, and the reason is worth knowing: it is the first
        // test declared in Fussy, and the worker runs a class's tests in declaration order, so when
        // that class runs as its own group it executes first and nothing has polluted the process
        // yet. Per-class is not blind to this defect —
        // it is blind to one where the *classmate that runs before it* is the culprit, which
        // IsolationSweepTests covers directly.
        results.InterferenceVictims.ShouldHaveSingleItem().DisplayName.ShouldEndWith(".Fussy.only_works_alone");
        results.FailedInBoth.ShouldHaveSingleItem().DisplayName.ShouldEndWith(".Basics.always_fails");
    }

    /// <summary>
    /// bobcat#577 over real workers: lazy pulls one class per <c>testing/runTests</c> request into each
    /// lane's long-lived process, and reaches the same verdicts as eager with the same processes.
    /// </summary>
    [Fact]
    public async Task lazy_distribution_reaches_the_same_verdicts_as_eager_on_the_same_number_of_processes()
    {
        async Task<SupervisorResults> run(Distribution distribution)
        {
            var supervisor = new Supervisor(factory(new Dictionary<string, string>
            {
                ["BOBCAT_FLAKY_STATE"] = tempFile()
            }))
            {
                Distribution = distribution,
                MaxParallelWorkers = 2,
                RetryBudget = new RetryBudget { MaxAttemptsPerTest = 3 }
            };

            return await supervisor.Run();
        }

        var eager = await run(Distribution.Eager);
        var lazy = await run(Distribution.Lazy);

        // The shape docs/parallel-ready-suites.md shows, over the real worker.
        var hostPath = workerPath;

        // #region supervisor-lazy
        var supervisor = new Supervisor(new MtpWorkerFactory(hostPath))
        {
            MaxParallelWorkers = 4,
            Distribution = Distribution.Lazy
        };
        // #endregion supervisor-lazy

        (await supervisor.Run()).Distribution.ShouldBe(Distribution.Lazy);

        lazy.Distribution.ShouldBe(Distribution.Lazy);
        lazy.Tests.Select(t => (t.DisplayName, t.Outcome))
            .ShouldBe(eager.Tests.Select(t => (t.DisplayName, t.Outcome)));
        named(lazy, "Fussy.flaky_until_second_attempt").Outcome.ShouldBe(RunOutcome.PassOnRetry);

        // One process per lane however many classes it pulled: no class costs a launch of its own.
        lazy.WorkersLaunched.ShouldBe(eager.WorkersLaunched);

        // Every batched class was pulled by some lane, whole.
        var pulled = lazy.LanePlan.SelectMany(l => l.Partitions).ToList();
        pulled.ShouldBeUnique();
        pulled.ShouldBe(eager.LanePlan.SelectMany(l => l.Partitions).Order(StringComparer.Ordinal), ignoreOrder: true);
    }

    /// <summary>The smallest host program docs/parallel-ready-suites.md shows, over the real worker.</summary>
    [Fact]
    public async Task the_smallest_host_program_runs_the_suite_on_lanes_with_a_retry_budget()
    {
        var hostPath = workerPath;

        // #region supervisor-host
        var supervisor = new Supervisor(new MtpWorkerFactory(hostPath))
        {
            MaxParallelWorkers = 4,
            RetryBudget = new RetryBudget { MaxAttemptsPerTest = 3 }
        };

        var results = await supervisor.Run();
        var exitCode = results.ExitCode;
        // #endregion supervisor-host

        results.Tests.ShouldNotBeEmpty();
        exitCode.ShouldBe(1); // the worker carries a test that always fails
    }
}
