using Bobcat;
using Bobcat.Alba;
using Bobcat.CritterStack;
using Bobcat.Runtime;

namespace OutboxDemo.Tests;

/// <summary>
/// Suite configuration for this spec project. No <c>Main</c>: the project references
/// <c>Bobcat.Mtp</c> and declares no entry point, so the generator emits the
/// Microsoft.Testing.Platform one and calls every <c>[BobcatConfiguration]</c> method.
/// Declaring none is also what keeps <c>AlbaResource&lt;Program&gt;</c> unambiguous — see
/// docs/resources.md.
/// </summary>
public static class SpecsRunner
{
    [BobcatConfiguration]
    public static void Configure(BobcatRunner runner)
    {
        // The reset hook is not optional here, and it is the point of the sample as much as
        // the endpoint is: the duplicate-registration scenario leaves a Registration behind,
        // and a Marten unique index on (MemberId, EventId) is persistent state. Without a
        // reset, this suite passes exactly once per database and then reports 409s for
        // registrations it believes are new — a sample that only works on a virgin database
        // is worse than no sample. ResetBetweenScenarios is where persistent state is
        // cleaned; the per-scenario DI scope opens over the top of it.
        runner.Resources.Add(new AlbaResource<Program>(reset: host => host.ResetEventStoresAsync()));
    }
}
