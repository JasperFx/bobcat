using Bobcat.TUnit;

namespace Bobcat.TUnit.Tests.Specs;

// bobcat#573: a spec's Event Model coordinates are TUnit properties on it, from the binding the
// generator registered, so a property filter selects it. It reads its own properties off the running
// test, which is the real runner's answer.
[BobcatFeature("Coordinates"), BobcatScenario]
[BobcatSlice(SliceName = "Selecting by coordinates", Chapter = "Selection", Domain = "Bobcat")]
public class CoordinateSpecs
{
    [Test]
    public async Task its_coordinates_are_its_properties()
    {
        // Given a spec bound to a slice
        // Then its slice is a property
        var properties = TestContext.Current!.Metadata.TestDetails.CustomProperties;

        await Assert.That(properties["Slice"]).IsEquivalentTo(["Selecting by coordinates"]);
        await Assert.That(properties["Chapter"]).IsEquivalentTo(["Selection"]);
        await Assert.That(properties["Domain"]).IsEquivalentTo(["Bobcat"]);
        await Assert.That(properties.ContainsKey("Pattern")).IsFalse();
    }
}
