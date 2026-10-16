using Bobcat.Xunit.Samples.Grammars;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The Acceptance lifecycle (bobcat#545): specifications written before the behaviour is finished.
/// They run every time; a failing one is recorded in full as <c>acceptance: failing</c> and reported
/// SKIPPED to xUnit, so the build stays green, and a passing one is ready to promote.
/// </summary>
/// <remarks>
/// Two failing shapes, because xUnit needs two mechanisms: a spec that fails only through gathered
/// wrongs finishes its body cleanly, while one that throws has already handed xUnit an exception.
/// <c>AcceptanceLifecycleEndToEndTests</c> runs this class in a real host and asserts on both, and on
/// what <c>BOBCAT_LIFECYCLE=strict</c> does to them.
/// </remarks>
[BobcatFeature("Acceptance specifications")]
public class AcceptanceSpecs
{
    private readonly FactGrammar _facts = new();

    [BobcatSpec(Lifecycle = Lifecycle.Acceptance)]
    public void a_wrong_is_gathered_before_the_behaviour_is_finished()
    {
        // Given the thing is activated
        _facts.TheThingIsActivated();

        // Then the line that is not true yet
        _facts.ThisLineIsAlwaysFalse();
    }

    [BobcatSpec(Lifecycle = Lifecycle.Acceptance)]
    public void the_behaviour_throws_because_it_does_not_exist_yet()
    {
        // Given the thing is activated
        _facts.TheThingIsActivated();

        // When the unfinished behaviour runs
        throw new NotImplementedException("the behaviour this specification describes");
    }

    [BobcatSpec(Lifecycle = Lifecycle.Acceptance)]
    public void an_acceptance_spec_that_passes_is_ready_to_promote()
    {
        // Then this line is always true
        _facts.ThisLineIsAlwaysTrue();
    }
}
