using Bobcat.Monitoring;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#783: a worker this suite launches never probes the shared console. The
/// <c>Bobcat.Supervisor.ProjectedWorker</c> runs are real runs — a one-test suite, bracketed,
/// published — and left to the defaults each one lands on the <c>bobcat ui</c> at
/// <c>localhost:5525</c> as a card of its own, from every worktree the suite is run in.
/// </summary>
public class WorkerMonitorOptOutTests
{
    [Fact]
    public void the_test_process_holds_the_kill_switch_so_every_worker_inherits_it()
    {
        // The module initializer in TestEnvironment. A worker's ProcessStartInfo starts as a copy of
        // this environment, so this one value is what every launch without an override gets.
        MonitorPublisher.Disabled.ShouldBeTrue(
            $"{MonitorPublisher.KillSwitchVariable} must be off in the test process; " +
            "a worker launched from it would otherwise probe the shared console");
    }

    [Fact]
    public async Task a_worker_launched_the_way_this_suite_launches_them_would_not_probe_the_shared_console()
    {
        // The plainest launch any test here makes — the worker, a factory, no monitor settings of
        // its own — then the worker's OWN publisher rule, evaluated inside the worker process and
        // written out, because the probe itself goes to a port this suite must never listen on.
        var probe = Path.Combine(Path.GetTempPath(), $"bobcat-monitor-probe-{Guid.NewGuid():N}.txt");
        try
        {
            var supervisor = new Supervisor(new MtpWorkerFactory(WorkerHosts.ProjectedWorker, new Dictionary<string, string>
            {
                ["BOBCAT_MONITOR_PROBE"] = probe
            }))
            {
                TestFilter = test => WorkerHosts.IsNamed(test.DisplayName, "Basics.passes")
            };

            var results = await supervisor.Run(TestContext.Current.CancellationToken);
            results.CleanPasses.Count.ShouldBe(1, results.Summarize());

            var decision = (await File.ReadAllLinesAsync(probe, TestContext.Current.CancellationToken))
                .Select(line => line.Split('=', 2))
                .ToDictionary(parts => parts[0], parts => parts[1]);

            var disabled = bool.Parse(decision["disabled"]);
            var targets = decision["urls"].Split(';', StringSplitOptions.RemoveEmptyEntries);

            (disabled || !targets.Contains(MonitorPublisher.DefaultUrl)).ShouldBeTrue(
                $"the worker would probe {MonitorPublisher.DefaultUrl}: disabled={disabled}, urls={decision["urls"]}");
        }
        finally
        {
            try { File.Delete(probe); } catch { }
        }
    }
}
