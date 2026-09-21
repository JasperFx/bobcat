using Bobcat;
using Bobcat.Alba;
using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BookingMonolith.Tests;

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
        // Nothing in this host carries a unique index, and every scenario mints fresh ids,
        // so the reset is not what makes the suite pass twice — it is what keeps the
        // document tables and the booking event store from growing without bound across
        // runs. Both halves are needed to actually empty it: the booking snapshots are
        // documents, but the events that produced them are not, and DeleteAllDocuments does
        // not touch the streams.
        runner.Resources.Add(new AlbaResource<Program>(reset: async host =>
        {
            var store = host.Services.GetRequiredService<IDocumentStore>();
            await store.Advanced.Clean.DeleteAllDocumentsAsync();
            await store.Advanced.Clean.DeleteAllEventDataAsync();
        }));
    }
}
