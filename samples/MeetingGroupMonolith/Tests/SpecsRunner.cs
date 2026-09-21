using Bobcat;
using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingGroupMonolith.Tests;

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
        runner.Resources.Add(new WebApp(reset: async host =>
        {
            var store = host.Services.GetRequiredService<IDocumentStore>();
            await store.Advanced.Clean.DeleteAllDocumentsAsync();
            await store.Advanced.Clean.DeleteAllEventDataAsync();
        }));
    }
}
