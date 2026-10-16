using Shouldly;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// A specification written once on a shared, generic, abstract base and run by every subclass
/// (bobcat#600) — the shape of Wolverine's <c>TransportCompliance&lt;T&gt;</c>, whose subclasses are
/// bodyless.
/// </summary>
/// <remarks>
/// <para>
/// Each subclass is its own feature: its identity, its listing entry and its run by identity are the
/// subclass's, while the steps the body declares are the base's. The body asserts both.
/// </para>
/// <para>
/// In the sample corpus rather than only in a unit test for the reason <see cref="OneAttributeSpecs"/>
/// is: <c>a_projected_listing_covers_every_specification_however_its_steps_are_declared</c> compares
/// the generator's listing with xUnit's own discovery, and xUnit discovers these once per subclass.
/// </para>
/// </remarks>
[BobcatFeature("Shared compliance")]
public abstract class SharedCompliance<TTransport>
{
    [BobcatSpec]
    public void each_subclass_is_its_own_feature()
    {
        // Given a test written once on a shared base
        var recording = ScenarioRecorder.Current.ShouldNotBeNull();

        // Then it records under the subclass that runs it, with the steps the base declares
        recording.Feature.ShouldBe(MarkerSpecNaming.FeatureTitle(GetType()));
        recording.Feature.ShouldNotBe("Shared compliance");
        recording.Declared.Count.ShouldBe(2);
    }
}

/// <summary>A subclass naming its own feature.</summary>
[BobcatFeature("Alpha transport")]
public class AlphaTransportCompliance : SharedCompliance<int>;

/// <summary>A subclass whose feature is its class name, read as a sentence.</summary>
public class BetaTransportCompliance : SharedCompliance<string>;
