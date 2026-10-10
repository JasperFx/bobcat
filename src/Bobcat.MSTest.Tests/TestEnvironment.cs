using System.Runtime.CompilerServices;
using Bobcat.Monitoring;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// Method-level parallelism, so the adapter is exercised the way a parallel suite runs it:
// several scenarios open at once on one shared set of hooks.
[assembly: Parallelize(Workers = 4, Scope = ExecutionScope.MethodLevel)]

namespace Bobcat.MSTest.Tests;

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
