using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// A projected assertion's sentence shows constants by value (bobcat#420): <c>nameof(Foo.Bar)</c>
/// reads as <c>"Bar"</c>, and a partial object as <c>T(Bar: v)</c> — only what was specified.
/// </summary>
public class ArgumentRenderingTests
{
    private static string sentence(string assertion)
    {
        var source = $$"""
                       using System;
                       using Bobcat;
                       using Shouldly;
                       using static Bobcat.Specifications;

                       namespace Specs;

                       public record Shipment(string TrackingNumber, string Carrier);

                       public sealed class FactAttribute : System.Attribute;

                       [BobcatFeature("Shipments")]
                       public class ShipmentSpecs
                       {
                           [Fact]
                           public void a_test()
                           {
                               var name = "TrackingNumber";
                               var shipment = new Shipment("1Z", "UPS");
                               {{assertion}}
                           }
                       }
                       """;

        var outcome = GeneratorHarness.Run(source, new Dictionary<string, string>
        {
            ["build_property.BobcatProjectAssertions"] = "true"
        });

        return outcome.GeneratedSource("BobcatStepInterceptors");
    }

    [Fact]
    public void nameof_renders_as_its_value()
    {
        sentence("name.ShouldBe(nameof(Shipment.TrackingNumber));")
            .ShouldContain("name should be \\\"TrackingNumber\\\"");
    }

    [Fact]
    public void a_partial_object_renders_only_what_it_specifies()
    {
        sentence("shipment.ShouldBe(Specify<Shipment>().With(x => x.TrackingNumber, \"1Z\").Build());")
            .ShouldContain("shipment should be Shipment(TrackingNumber: \\\"1Z\\\")");
    }

    [Fact]
    public void an_ordinary_argument_is_unchanged()
    {
        sentence("name.ShouldBe(\"Ann\");").ShouldContain("name should be \\\"Ann\\\"");
    }
}
