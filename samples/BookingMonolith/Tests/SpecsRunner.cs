using Bobcat.Alba;
using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BookingMonolith.Tests;

/// <summary>
/// Bobcat spec-runner entry point. An explicit Main class rather than top-level statements,
/// because this project references the host — which uses top-level statements and synthesizes
/// its own <c>Program</c> in the global namespace. Two of those in one assembly make
/// <c>AlbaResource&lt;Program&gt;</c> bind to the runner stub instead of the web app, which
/// surfaces as a native PAL crash with no managed stack. See docs/sample-wiring.md footgun 1.
/// </summary>
public static class SpecsRunner
{
    public static Task<int> Main(string[] args)
        => BobcatRunner.Run(args, runner =>
        {
            // Resolves unambiguously to the host's entry point, given the explicit Main above.
            //
            // Nothing in this host carries a unique index, and every scenario mints fresh ids,
            // so the reset is not what makes the suite pass twice — it is what keeps the
            // document tables and the booking event store from growing without bound across
            // runs. Both halves are needed to actually empty it: the booking snapshots are
            // documents, but the events that produced them are not, and DeleteAllDocuments does
            // not touch the streams.
            runner.Suite.AddResource(new AlbaResource<Program>(reset: async host =>
            {
                var store = host.Services.GetRequiredService<IDocumentStore>();
                await store.Advanced.Clean.DeleteAllDocumentsAsync();
                await store.Advanced.Clean.DeleteAllEventDataAsync();
            }));
            runner.ScanForFeatures(typeof(BookingMonolithFixture).Assembly);
        });
}
