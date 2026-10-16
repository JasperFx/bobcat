using Bobcat.TUnit;

namespace Bobcat.TUnit.Tests.Lifecycle;

// The Acceptance lifecycle on TUnit (bobcat#545). In an ordinary run of this project these are
// reported SKIPPED (failing) and passed — never failed — which is itself the check: the adapter
// turning a failure into a skip is what keeps this project green. TUnitAcceptanceLifecycleTests in
// Bobcat.Supervisor.Tests runs this namespace alone and asserts on the platform's summary, plain
// and under BOBCAT_LIFECYCLE=strict.

[BobcatFeature("Acceptance on TUnit"), BobcatScenario(Lifecycle = Bobcat.Lifecycle.Acceptance)]
public class AcceptanceSpecs
{
    [Test]
    public Task the_behaviour_throws_because_it_does_not_exist_yet()
    {
        // Given a ledger
        // When the unfinished behaviour runs
        throw new NotImplementedException("the behaviour this specification describes");
    }

    [Test]
    public Task an_acceptance_spec_that_passes_is_ready_to_promote()
    {
        // Then it is true
        return Task.CompletedTask;
    }
}
