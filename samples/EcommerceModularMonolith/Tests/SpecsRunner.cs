using Bobcat.Alba;
using Bobcat.Runtime;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace EcommerceModularMonolith.Tests;

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
            // The reset hook empties every module's schema (catalog, basket, ordering, discount
            // are all Marten document tables, so one call reaches all four). It is load-bearing
            // for the Ordering scenarios, which look up "the order created by the checkout" via
            // GET /orders — on a database carrying orders from a previous run, "the order" is
            // whichever one sorts first, and a delete would hit the wrong document. Baskets are
            // keyed by user name and upserted, so they do not need it; orders do.
            //
            // The host's own seed data (three products, two coupons) DOES run under Alba — the
            // Program.cs seeding after builder.Build() executes at host start, which the Marten
            // schema log shows (catalog and discount tables appear before "Application started").
            // This hook is what removes it, so "at least 1 catalog product is returned" is
            // satisfied by the product the scenario created and not by the seed. See
            // docs/sample-wiring.md footgun 10.
            runner.Suite.AddResource(new AlbaResource<Program>(reset: async host =>
            {
                var store = host.Services.GetRequiredService<IDocumentStore>();
                await store.Advanced.Clean.DeleteAllDocumentsAsync();
            }));
            runner.ScanForFeatures(typeof(EcommerceModularMonolithFixture).Assembly);
        });
}
