using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#625: a projected spec written in the scenario vocabulary — <c>CritterStackSpec</c>'s, read from
/// another assembly's metadata (<see cref="CritterStackStandIn"/>, since bobcat#744 moved the real one to
/// WolverineFx.Bobcat), and a derived scenario's — registers
/// its vocabulary calls for the preview, which previously showed only <c>[Then]</c> helpers and marker
/// comments.
/// </summary>
public class VocabularyPreviewTests
{
    private const string Source =
        """
        using System;
        using System.Threading.Tasks;
        using Bobcat;
        using Bobcat.CritterStack;
        using Xunit;

        namespace Shipping
        {
            public record ShipOrder(Guid Id);
            public record ShipmentCreated(Guid Id);
            public record ShipmentPacked(Guid Id);
            public class Shipment { public Guid Id { get; set; } }

            // A derived scenario, as WolverineScenario derives from CritterStackScenario in another repository
            public class MessagingScenario : CritterStackScenario
            {
                public MessagingScenario() : base((IServiceProvider)null!) { }
                public Task WhenReceived(object message) => Task.CompletedTask;
            }

            // Named like vocabulary, but nothing about it is: never shown
            public class Unrelated
            {
                public void GivenAnything() { }
            }

            public class Checks
            {
                [Then("the act completed")]
                public void ThenTheActCompleted() { }
            }
        }

        namespace Specs
        {
            using Shipping;

            [BobcatFeature("Shipping")]
            public class shipping : CritterStackSpec
            {
                public shipping() : base((IServiceProvider)null!) { }

                [Fact]
                public async Task an_order_is_shipped()
                {
                    var id = Guid.NewGuid();
                    await GivenEvents<Shipment>(id, new ShipmentCreated(id), new ShipmentPacked(id));
                    await new MessagingScenario().WhenReceived(new ShipOrder(id));
                    new Unrelated().GivenAnything();
                    await ThenDocument<Shipment>(id);
                    new Checks().ThenTheActCompleted();
                }
            }
        }
        """;

    [Fact]
    public void vocabulary_calls_are_registered_for_the_preview_in_source_order()
    {
        var outcome = GeneratorHarness.Run(Source, null, [CritterStackStandIn.Reference]);
        outcome.CompilationErrors.ShouldBeEmpty();

        var registration = outcome.GeneratedSource("BobcatDeclaredSteps");
        var line = registration.Split('\n').Single(x => x.Contains("VocabularySteps.Register("));

        line.ShouldContain("VocabularySteps.Register(\"Shipping/an order is shipped\"");
        line.ShouldContain("""new global::Bobcat.VocabularyStep("Given", "events Shipment, ShipmentCreated, ShipmentPacked", "CritterStackSpec.GivenEvents",""");
        line.ShouldContain("""new global::Bobcat.VocabularyStep("When", "received ShipOrder", "MessagingScenario.WhenReceived",""");
        line.ShouldContain("""new global::Bobcat.VocabularyStep("Then", "document Shipment", "CritterStackSpec.ThenDocument",""");

        // Not vocabulary, and a [Then] helper is a planned step already
        line.ShouldNotContain("GivenAnything");
        line.ShouldNotContain("ThenTheActCompleted");

        line.IndexOf("GivenEvents", StringComparison.Ordinal)
            .ShouldBeLessThan(line.IndexOf("WhenReceived", StringComparison.Ordinal));
        line.IndexOf("WhenReceived", StringComparison.Ordinal)
            .ShouldBeLessThan(line.IndexOf("ThenDocument", StringComparison.Ordinal));
    }
}
