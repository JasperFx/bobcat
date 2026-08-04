using Bobcat.Alba;
using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace PaymentsMonolith.Tests;

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
            // The reset hook is not optional. Registration is guarded by a uniqueness check on
            // email (RegisterUserEndpoint.ValidateAsync returns 409 for an address already
            // stored), so every user this suite registers is persistent state that changes what
            // the next run means. Without a reset the suite passes exactly once per database and
            // then reports 409s for registrations it believes are new.
            runner.Suite.AddResource(new AlbaResource<Program>(reset: async host =>
            {
                var store = host.Services.GetRequiredService<IDocumentStore>();
                await store.Advanced.Clean.DeleteAllDocumentsAsync();
            }));
            runner.ScanForFeatures(typeof(PaymentsMonolithFixture).Assembly);
        });
}
