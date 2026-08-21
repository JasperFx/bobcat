using Bobcat.Alba;
using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace MoreSpeakers.Tests;

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
            // The reset hook is load-bearing. POST /api/speakers refuses a duplicate email with
            // a 409, and every scenario registers speakers under fixed addresses
            // ("speaker@conf.com", "mentor@conf.com", ...). Without a reset the suite passes
            // exactly once per database and then every registration is a 409 for a speaker it
            // believes is new. Same shape as PaymentsMonolith's unique index on email, for the
            // same reason. ResetBetweenScenarios is where persistent state is cleaned; the
            // per-scenario DI scope opens over the top of it.
            runner.Suite.AddResource(new AlbaResource<Program>(reset: async host =>
            {
                var store = host.Services.GetRequiredService<IDocumentStore>();
                await store.Advanced.Clean.DeleteAllDocumentsAsync();
            }));
            runner.ScanForFeatures(typeof(MoreSpeakersFixture).Assembly);
        });
}
