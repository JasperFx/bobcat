using System.Runtime.CompilerServices;
using Bobcat.Monitoring;

namespace Bobcat.Supervisor.ProjectedWorker;

internal static class TestEnvironment
{
    /// <summary>
    /// This worker's specifications fail, hang and crash on purpose, and every run of it is a real
    /// projected run that would probe <c>localhost:5525</c> and put a red card on whatever
    /// <c>bobcat ui</c> is there (bobcat#783). When nobody has said whether a console is wanted, the
    /// answer is no. A launcher that wants the run published says so — <c>BOBCAT_MONITOR=1</c> with
    /// <c>BOBCAT_MONITOR_URL</c> at a console it owns — and that explicit value is left alone.
    /// </summary>
    [ModuleInitializer]
    internal static void NoConsoleUnlessAsked()
    {
        if (Environment.GetEnvironmentVariable(MonitorPublisher.KillSwitchVariable) is null)
        {
            Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, "0");
        }
    }
}
