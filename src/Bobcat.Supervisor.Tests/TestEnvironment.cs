using System.Runtime.CompilerServices;
using Bobcat.Monitoring;

namespace Bobcat.Supervisor.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Keep the suite hermetic (bobcat#783): every worker this suite launches inherits this
    /// process's environment, and without the kill switch each one probes <c>localhost:5525</c> for
    /// a console and publishes its one-test run to whatever is listening — which on a development box
    /// is a real <c>bobcat ui</c>. One such board held about 400 <c>Bobcat.Supervisor.ProjectedWorker</c>
    /// runs from forty worktrees. CI sets <c>BOBCAT_MONITOR=0</c> for the whole job; this is the same
    /// setting for a local run, so the two behave alike.
    /// </summary>
    /// <remarks>
    /// Set unconditionally, as <c>Bobcat.Xunit.Tests</c> and <c>Bobcat.NUnit.Tests</c> do: a shell
    /// that exports <c>BOBCAT_MONITOR=1</c> for its own specs must not turn this suite's workers
    /// into publishers. A test that WANTS a worker to publish gives that worker its own console —
    /// <c>BOBCAT_MONITOR_URL</c> at a test-owned host plus <c>BOBCAT_MONITOR=1</c> in the worker's
    /// environment, which is layered over this process's — and
    /// <see cref="WorkerMonitorOptOutTests"/> is what fails if a worker launched any other way
    /// would still reach for the shared console.
    /// </remarks>
    [ModuleInitializer]
    internal static void SuppressTheMonitorProbe()
        => Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, "0");
}
