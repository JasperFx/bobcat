using Bobcat.Alba;
using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BankAccountES.Tests;

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
            // Verified NOT load-bearing, and recorded as such so nobody has to re-derive it:
            // the suite passes twice in a row with this hook emptied, because every scenario
            // mints a fresh client and account id, so no run can collide with the last one.
            // Unlike PaymentsMonolith, whose unique index on email makes its reset the
            // difference between a suite that works once per database and one that works.
            //
            // It is kept anyway, for the one reason that does apply: without it the event store
            // grows without bound across runs, and a spec suite that quietly accumulates rows
            // gets slower for reasons no scenario explains.
            //
            // Both halves are needed to actually empty it. The snapshots are documents, but the
            // deposits and withdrawals that produced them are events, and DeleteAllDocuments
            // does not touch the streams.
            runner.Suite.AddResource(new AlbaResource<Program>(reset: async host =>
            {
                var store = host.Services.GetRequiredService<IDocumentStore>();
                await store.Advanced.Clean.DeleteAllDocumentsAsync();
                await store.Advanced.Clean.DeleteAllEventDataAsync();
            }));
            runner.ScanForFeatures(typeof(BankAccountESFixture).Assembly);
        });
}
