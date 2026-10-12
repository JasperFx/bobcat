using Bobcat.TUnit;
using Bobcat.TUnit.Tests.Filtering;

namespace Bobcat.TUnit.Tests.Specs;

// A projected Bobcat suite on TUnit (bobcat#470): what the resident runner lists, and then runs by
// identity. The shapes are the ones a tree-node filter has to tell apart — two methods in one class,
// the same method name on two classes, and a nested class — and each test records its name to the
// BOBCAT_TUNIT_PROBE file, so a driving test sees exactly what a filtered run executed.

[BobcatFeature("Ledger"), BobcatScenario]
public class LedgerSpecs
{
    [Test]
    public async Task opening_a_ledger()
    {
        // Given a new ledger
        // Then it is open
        await Probe.Record("LedgerSpecs.opening_a_ledger");
    }

    [Test]
    public async Task closing_a_ledger()
    {
        // Given an open ledger
        // When it is closed
        // Then it is closed
        await Probe.Record("LedgerSpecs.closing_a_ledger");
    }
}

[BobcatFeature("Audit"), BobcatScenario]
public class AuditSpecs
{
    [Test]
    public async Task opening_a_ledger()
    {
        // Given a new ledger
        // Then the opening is audited
        await Probe.Record("AuditSpecs.opening_a_ledger");
    }

    [Test]
    public async Task closing_a_ledger()
    {
        // Given an open ledger
        // When it is closed
        // Then the closing is audited
        await Probe.Record("AuditSpecs.closing_a_ledger");
    }

    [BobcatFeature("Audit trail"), BobcatScenario]
    public class Trail
    {
        [Test]
        public async Task is_kept()
        {
            // Given an audited ledger
            // Then its trail is kept
            await Probe.Record("AuditSpecs.Trail.is_kept");
        }
    }
}
