using Bobcat;
using Bobcat.Alba;
using Bobcat.CritterStack;
using Bobcat.Runtime;

namespace PaymentsMonolith.Tests;

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
        // The reset hook is not optional. Registration is guarded by a uniqueness check on
        // email (RegisterUserEndpoint.ValidateAsync returns 409 for an address already
        // stored), so every user this suite registers is persistent state that changes what
        // the next run means. Without a reset the suite passes exactly once per database and
        // then reports 409s for registrations it believes are new.
        runner.Resources.Add(new AlbaResource<Program>(reset: host => host.ResetEventStoresAsync()));
    }
}
