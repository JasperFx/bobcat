using Bobcat;
using Bobcat.Alba;
using Bobcat.CritterStack;
using Bobcat.Runtime;

namespace EcommerceModularMonolith.Tests;

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
        runner.Resources.Add(new AlbaResource<Program>(reset: host => host.ResetEventStoresAsync()));
    }
}
