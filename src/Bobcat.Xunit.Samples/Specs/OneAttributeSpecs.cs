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
/// <c>ProjectedSpecIdentityEndToEndTests.a_projected_listing_covers_every_specification_however_its_steps_are_declared</c>
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

        var uid = ScenarioRecorder.Current?.Uid;

        uid.ShouldNotBeNull();
        uid.ShouldBe("Facts, in one attribute/the implied scenario is the one the steps land on");
    }

    /// <summary>
    /// An explicit scenario name (bobcat#489): the identity is the name, verbatim, punctuation and
    /// all, instead of the method name read as a sentence. A method name cannot spell the apostrophe,
    /// the dash or the comma.
    /// </summary>
    /// <remarks>
    /// <c>ProjectedSpecIdentityEndToEndTests</c> lists this identity from the manifest and then runs it
    /// by name, so the assertion below is what proves the recorder published the same string the
    /// generator bound.
    /// </remarks>
    [BobcatSpec(Name = ExplicitName)]
    public void named_explicitly()
    {
        _facts.TheThingIsActivated();

        ScenarioRecorder.Current?.Uid.ShouldBe("Facts, in one attribute/" + ExplicitName);
    }

    public const string ExplicitName = "a spec's name can't always be a method name -- re-stated, verbatim";

    /// <summary>
    /// bobcat#573: the spec's Event Model coordinates are xUnit traits on it, from the binding the
    /// generator registered — so <c>dotnet test --filter-trait "Slice=Selecting by coordinates"</c>
    /// selects it. It reads its own traits off the running test, which is the real runner's answer, not
    /// an attribute instance's; <c>ProjectedSpecIdentityEndToEndTests</c> selects it by that trait.
    /// </summary>
    [BobcatSpec(SliceName = "Selecting by coordinates", Chapter = "Selection", Domain = "Bobcat")]
    [Trait("Category", "Coordinates")]
    public void its_coordinates_are_its_traits()
    {
        _facts.TheThingIsActivated();

        var traits = global::Xunit.TestContext.Current.Test!.Traits;
        traits["Slice"].ShouldBe(["Selecting by coordinates"]);
        traits["Chapter"].ShouldBe(["Selection"]);
        traits["Domain"].ShouldBe(["Bobcat"]);
        traits["Category"].ShouldBe(["Coordinates"]);

        // No vocabulary role and no declared pattern, so nothing can say what pattern it is
        traits.ContainsKey("Pattern").ShouldBeFalse();
    }

    /// <summary>
    /// Stub-first (issue #404): the specification is written before the behaviour exists. It is
    /// SKIPPED, so the throw below never happens, and it reaches the Event Model as a
    /// <c>PendingSpecification</c> hotspot rather than as evidence the slice is verified.
    /// </summary>
    /// <remarks>
    /// Deliberately left in the corpus rather than written and deleted: a skipped test is a shape
    /// the listing, the manifest and the identity audit all have to keep handling, and nothing
    /// here would notice if one of them stopped.
    /// </remarks>
    [BobcatSpec(Pending = true)]
    public void a_pending_specification_is_declared_before_its_behaviour_exists()
        => throw new NotImplementedException("the behaviour this specification describes");
}
