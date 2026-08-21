using Bobcat.Alba;
using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingGroupMonolith.Tests;

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
            // Verified NOT load-bearing for correctness, and recorded as such so nobody has to
            // re-derive it: the suite passes twice in a row with this hook emptied. Nothing in
            // this host has a unique index, and every scenario registers its own user and
            // proposes its own group, so no run can collide with the last one.
            //
            // It is kept for the one reason that does apply: the "is listed" assertions read
            // whole collections, and without a reset those collections grow by one group and
            // one meeting on every run. The assertions would still pass, but the suite would
            // slowly stop meaning what it says. Both halves are needed — the Payments module is
            // event-sourced, and DeleteAllDocuments does not touch the streams.
            runner.Suite.AddResource(new AlbaResource<Program>(reset: async host =>
            {
                var store = host.Services.GetRequiredService<IDocumentStore>();
                await store.Advanced.Clean.DeleteAllDocumentsAsync();
                await store.Advanced.Clean.DeleteAllEventDataAsync();
            }));
            runner.ScanForFeatures(typeof(MeetingGroupMonolithFixture).Assembly);
        });
}
