using Bobcat.EventModel;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace Bobcat.EventModel.Tests;

public class CuratedModelMapperTests
{
    private static CuratedModelFile parse(string yaml)
    {
        var reading = CuratedModelReader.Read(yaml);
        reading.Problems.ShouldBeEmpty();
        return reading.File!;
    }

    private const string Full =
        """
        schema: 1
        model: CritterCrush
        namespace: CritterCrush
        slices:
          - name: SwipeOnDog
            pattern: Command
            domain: Discovery
            trigger: { kind: Human, label: Discovery Feed }
            command: SwipeOnDog
            handler: SwipeOnDogEndpoint
            aggregates: [Match]
            events: [DogLiked]
            messages: [MatchDetected]
            externalSystems:
              - { name: Supabase Auth, direction: Inbound }
            hotspots: ["Simultaneous swipes?"]
            specifications:
              feature: Swiping
              scenarios:
                - name: A like is recorded
                  then: [{ event: DogLiked }]
        """;

    [Fact]
    public void every_declared_role_is_stamped()
    {
        var descriptor = CuratedModelMapper.ToDescriptor(parse(Full));

        descriptor.Name.ShouldBe("CritterCrush");
        var slice = descriptor.Slices.Single();

        slice.Name.ShouldBe("SwipeOnDog");
        slice.Pattern.ShouldBe(SlicePattern.Command);
        slice.TriggerKind.ShouldBe(TriggerKind.Human);
        slice.TriggerLabel.ShouldBe("Discovery Feed");
        slice.Domain.ShouldBe("Discovery");
        slice.CommandType!.Name.ShouldBe("SwipeOnDog");
        slice.HandlerType!.Name.ShouldBe("SwipeOnDogEndpoint");
        slice.AggregateTypes.Single().Name.ShouldBe("Match");
        slice.EmittedEvents.Single().Name.ShouldBe("DogLiked");
        slice.PublishedMessages.Single().Name.ShouldBe("MatchDetected");
        slice.ExternalSystems.Single().ShouldBe(new ExternalSystemDescriptor("Supabase Auth", ExternalSystemDirection.Inbound));
    }

    [Fact]
    public void declared_types_are_name_only_with_a_synthesized_full_name()
    {
        var slice = CuratedModelMapper.ToDescriptor(parse(Full)).Slices.Single();

        // The type does not exist yet: the FullName is {namespace}.{name} so drift matching can
        // join it to the generated CLR type later, and the assembly is deliberately empty.
        slice.CommandType.ShouldBe(new JasperFx.Descriptors.TypeDescriptor("SwipeOnDog", "CritterCrush.SwipeOnDog", string.Empty));
    }

    [Fact]
    public void without_a_namespace_the_full_name_is_the_bare_name()
    {
        var file = parse("schema: 1\nmodel: X\nslices: [{ name: A, command: DoIt }]");

        CuratedModelMapper.ToDescriptor(file).Slices.Single().CommandType!.FullName.ShouldBe("DoIt");
    }

    [Fact]
    public void specification_identities_are_feature_slash_scenario()
    {
        var slice = CuratedModelMapper.ToDescriptor(parse(Full)).Slices.Single();

        // ⚠️ THE load-bearing assertion of this type: this exact string is what joins the
        // descriptor binding, Bobcat run evidence, and a Stoat spec-identity gate.
        slice.Specifications.Single().Identity.ShouldBe("Swiping/A like is recorded");
    }

    [Fact]
    public void the_feature_half_defaults_to_the_slice_name()
    {
        var file = parse(
            """
            schema: 1
            model: X
            slices:
              - name: SwipeOnDog
                specifications:
                  scenarios: [{ name: S }]
            """);

        CuratedModelMapper.ToDescriptor(file).Slices.Single()
            .Specifications.Single().Identity.ShouldBe("SwipeOnDog/S");
    }

    [Fact]
    public void hotspots_arrive_as_prose()
    {
        var slice = CuratedModelMapper.ToDescriptor(parse(Full)).Slices.Single();

        slice.Hotspots.Single().ShouldBe(HotspotDescriptor.Prose("Simultaneous swipes?"));
    }

    [Fact]
    public void roles_only_the_graph_stays_computed_upstream()
    {
        // Not a tautology: Elements/Edges being non-empty here proves the mapper never needed
        // to stamp a graph — the computed-on-read contract renders declared roles by itself.
        var slice = CuratedModelMapper.ToDescriptor(parse(Full)).Slices.Single();

        slice.Elements.ShouldContain(x => x.Kind == EventModelElementKind.Command);
        slice.Edges.ShouldNotBeEmpty();
    }
}
