using Marten;
using Microsoft.Extensions.DependencyInjection;
using Bobcat.Runtime;
using Bobcat.Alba;

namespace OutboxDemo.Tests;

/// <summary>
/// Bobcat spec-runner entry point. An explicit Main class rather than top-level statements,
/// because this project references the host — which uses top-level statements and synthesizes
/// its own <c>Program</c> in the global namespace. Two of those in one assembly make
/// <c>AlbaResource&lt;Program&gt;</c> bind to the runner stub instead of the web app, which
/// surfaces as a native PAL crash with no managed stack.
/// </summary>
public static class SpecsRunner
{
    public static Task<int> Main(string[] args)
        => BobcatRunner.Run(args, runner =>
        {
            // Resolves unambiguously to the host's entry point, given the explicit Main above.
            //
            // The reset hook is not optional here, and it is the point of the sample as much as
            // the endpoint is: the duplicate-registration scenario leaves a Registration behind,
            // and a Marten unique index on (MemberId, EventId) is persistent state. Without a
            // reset, this suite passes exactly once per database and then reports 409s for
            // registrations it believes are new — a sample that only works on a virgin database
            // is worse than no sample. ResetBetweenScenarios is where persistent state is
            // cleaned; the per-scenario DI scope opens over the top of it.
            runner.Suite.AddResource(new AlbaResource<Program>(reset: async host =>
            {
                var store = host.Services.GetRequiredService<IDocumentStore>();
                await store.Advanced.Clean.DeleteAllDocumentsAsync();
            }));
            runner.ScanForFeatures(typeof(OutboxDemoFixture).Assembly);
        });
}
