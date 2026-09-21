using Bobcat;
using Bobcat.CritterStack;
using Bobcat.Runtime;

namespace BankAccountES.Tests;

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
        // ResetEventStoresAsync empties every store the host registers — documents AND event
        // streams, which is what it takes here: the snapshots are documents, but the deposits
        // and withdrawals that produced them are events, and deleting one half leaves the other.
        //
        // It reaches the store through JasperFx.Events.IEventStore, which Marten, Polecat and
        // Fisher all register, so this file has no `using Marten` and would read the same
        // against either of the others. Bobcat.CritterStack, issue #103.
        runner.Resources.Add(new WebApp(reset: host => host.ResetEventStoresAsync()));
    }
}
