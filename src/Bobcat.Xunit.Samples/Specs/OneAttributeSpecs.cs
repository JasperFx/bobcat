using Bobcat.Xunit.Samples.Grammars;
using Shouldly;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The one-attribute projected spec (issue #403): <c>[BobcatSpec]</c> is the <c>[Fact]</c>, it
/// opens the recording, and it carries the slice binding, so a class needs only
/// <c>[BobcatFeature]</c> for its title.
/// </summary>
/// <remarks>
/// <para>
/// Every other class in this corpus carries <c>[BobcatFeature(…), BobcatScenario]</c> and
/// <c>[Fact]</c> per test. Both forms stay supported, and keeping one class in each is the point:
/// a change that broke either would otherwise go unnoticed here.
/// </para>
/// <para>
/// <b>Why this class is in the sample corpus and not only in a unit test.</b>
/// <c>SpecIdentityEndToEndTests.a_projected_listing_covers_every_specification_however_its_steps_are_declared</c>
/// reads the test count from the platform and asserts the spec manifest matches it. The generator
/// recognises <c>[BobcatSpec]</c> by simple name and xUnit recognises it by base class, which are
/// two independent mechanisms — so if either stopped seeing it, that test goes red with the two
/// numbers disagreeing. A unit test over the generator alone could not catch the half it does not
/// own.
/// </para>
/// </remarks>
[BobcatFeature("Facts, in one attribute")]
public class OneAttributeSpecs
{
    private readonly FactGrammar _facts = new();

    [BobcatSpec]
    public void a_spec_needs_no_fact_attribute_beside_it()
    {
        // Given the thing is activated
        _facts.TheThingIsActivated();

        // Then the ledger balances
        _facts.ThisLineIsAlwaysTrue().ShouldBeTrue();
    }

    /// <summary>
    /// The scenario is open inside the body, from the method's own attribute — there is no
    /// <c>[BobcatScenario]</c> anywhere on this class.
    /// </summary>
    [BobcatSpec]
    public void the_implied_scenario_is_the_one_the_steps_land_on()
    {
        _facts.TheThingIsActivated();

        // Deliberately not `ShouldNotBeNull()`: the Shouldly interceptor the generator emits for
        // this corpus cannot currently intercept `ShouldNotBeNull<T>` over a nullable reference
        // type (CS9144, signatures do not match) — a pre-existing generator limitation unrelated
        // to this attribute, and not something a sample should carry a workaround comment for
        // silently.
        var uid = ScenarioRecorder.Current?.Uid;

        uid.ShouldBe("Facts, in one attribute/the implied scenario is the one the steps land on");
    }
}
