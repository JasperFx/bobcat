using System.Runtime.CompilerServices;
using Bobcat.Monitoring;
using NUnit.Framework;

// Enough workers that ParallelScenarioTests' three tests can all be in flight at once.
[assembly: LevelOfParallelism(4)]

namespace Bobcat.NUnit.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Keep the suite hermetic: no probe for a Bobcat console, and no projected spec report — the
    /// recordings here are fixtures for testing the adapter, not specifications anybody reads.
    /// </summary>
    [ModuleInitializer]
    internal static void Hermetic()
    {
        Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, "0");
        Environment.SetEnvironmentVariable(ProjectedSpecConsole.EnvironmentVariable, "0");
    }
}
