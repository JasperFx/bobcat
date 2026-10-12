using Shouldly;

namespace Bobcat.Supervisor.Tests;

// Phase 3: delete with SampleWorker

/// <summary>
/// The end-to-end assertions only the Gherkin-lane <c>Bobcat.Supervisor.SampleWorker</c> can
/// produce. Everything else moved to <see cref="SupervisorEndToEndTests"/>, which drives the
/// projected worker; this class goes when <c>Bobcat.Mtp</c> does (bobcat#471, Phase 3).
/// </summary>
public class SampleWorkerEndToEndTests
{
    [Fact]
    public async Task a_gherkin_resource_that_fails_to_start_is_reported_as_an_error_not_a_crash()
    {
        // Issue #123, in the host it was fixed in. The worker's broker ITestResource refuses to
        // start, so nothing in it can run. Before the fix the SpecCatastrophicException escaped the
        // MTP host, the process died, and the supervisor could only call every test Indeterminate
        // with a worker fault. What only this host can produce is the ERROR state on every
        // scenario: the projected twin in SupervisorEndToEndTests gets xUnit's own failure for a
        // broken assembly fixture instead.
        var workerPath = WorkerHosts.SampleWorker;
        File.Exists(workerPath).ShouldBeTrue($"The sample worker was not built at {workerPath}");

        var supervisor = new Supervisor(new MtpWorkerFactory(workerPath, new Dictionary<string, string>
        {
            ["BOBCAT_START_FAILS"] = "true"
        }));

        var results = await supervisor.Run();

        results.WorkerFaults.ShouldBeEmpty();
        results.Indeterminate.ShouldBeEmpty();

        results.Tests.Count.ShouldBe(8);
        results.Failed.Count.ShouldBe(8);
        results.Failed.ShouldAllBe(t => t.Final.Outcome.State == WorkerTestState.Error);
        results.Failed.ShouldAllBe(t => t.Final.Outcome.ErrorMessage!.Contains("the broker refused the connection"));

        results.ExitCode.ShouldBe(1);
    }
}
