using Bobcat;
using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace PaymentsMonolith.Tests;

/// <summary>
/// Suite configuration for this spec project. No <c>Main</c>: the project references
/// <c>Bobcat.Mtp</c> and declares no entry point, so the generator emits the
/// Microsoft.Testing.Platform one and calls every <c>[BobcatConfiguration]</c> method.
/// Declaring none is also what keeps <c>WebApp</c>'s unqualified <c>Program</c> unambiguous —
/// see docs/resources.md.
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
        runner.Resources.Add(new WebApp(reset: async host =>
        {
            var store = host.Services.GetRequiredService<IDocumentStore>();
            await store.Advanced.Clean.DeleteAllDocumentsAsync();
        }));
    }
}
