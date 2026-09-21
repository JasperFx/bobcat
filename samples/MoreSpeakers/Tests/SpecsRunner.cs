using Bobcat;
using Bobcat.Alba;
using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace MoreSpeakers.Tests;

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
        // The reset hook is load-bearing. POST /api/speakers refuses a duplicate email with
        // a 409, and every scenario registers speakers under fixed addresses
        // ("speaker@conf.com", "mentor@conf.com", ...). Without a reset the suite passes
        // exactly once per database and then every registration is a 409 for a speaker it
        // believes is new. Same shape as PaymentsMonolith's unique index on email, for the
        // same reason. ResetBetweenScenarios is where persistent state is cleaned; the
        // per-scenario DI scope opens over the top of it.
        runner.Resources.Add(new AlbaResource<Program>(reset: async host =>
        {
            var store = host.Services.GetRequiredService<IDocumentStore>();
            await store.Advanced.Clean.DeleteAllDocumentsAsync();
        }));
    }
}
