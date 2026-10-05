using Bobcat.Xunit.Samples.Grammars;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// Storyteller's VerifyObject samples from the C# side. Two of these four fail on purpose, because
/// the samples exist to show what a property check looks like when it disagrees.
/// </summary>
[BobcatFeature("Objects"), BobcatScenario]
public class ObjectSpecs
{
    private readonly ObjectsGrammar _objects = new();

    /// <summary>
    /// Samples/Specs/Create Objects/Using_VerifyObject.md — three of the Address's six fields are
    /// named, and the document means nothing by the other three.
    /// </summary>
    [Fact]
    public void only_the_properties_the_document_names_are_compared()
    {
        _objects.TheAddressIs("""
            | Address1     | Address2 | City   | StateOrProvince | Country | PostalCode |
            | 3 1st Street | EMPTY    | Dallas | TX              | US      | 75201      |
            """);

        _objects.TheAddressShouldBe("""
            | Address1     | Address2 | City   |
            | 3 1st Street | EMPTY    | Dallas |
            """);
    }

    /// <summary>StoryTeller.Samples/Specs/General/Check properties.md — all correct.</summary>
    [Fact]
    public void every_named_property_agrees()
    {
        _objects.TheAddressIs("""
            | Address1      | City   | StateOrProvince |
            | 2 Second Lane | Austin | TX              |
            """);

        _objects.TheAddressShouldBe("""
            | Address1      | City   | StateOrProvince |
            | 2 Second Lane | Austin | TX              |
            """);
    }

    /// <summary>One column wrong, so the grid says which rather than a reader parsing a sentence.</summary>
    [Fact]
    public void one_property_disagrees()
    {
        _objects.TheAddressIs("""
            | Address1      | City   | StateOrProvince |
            | 2 Second Lane | Austin | TX              |
            """);

        _objects.TheAddressShouldBe("""
            | Address1      | City   | StateOrProvince |
            | 2 Second Lane | Dallas | TX              |
            """);
    }

    /// <summary>The same document with every column wrong.</summary>
    [Fact]
    public void every_property_disagrees()
    {
        _objects.TheAddressIs("""
            | Address1      | City   | StateOrProvince |
            | 2 Second Lane | Austin | TX              |
            """);

        _objects.TheAddressShouldBe("""
            | Address1    | City    | StateOrProvince |
            | 9 Ninth Way | Houston | OK              |
            """);
    }
}
