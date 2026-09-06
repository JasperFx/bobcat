using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Tests;

public class CuratedModelReaderTests
{
    private const string Valid =
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
            events: [DogLiked, DogPassed]
            readModels: []
            hotspots: ["What happens when both swipe simultaneously?"]
            specifications:
              scenarios:
                - name: A like is recorded
                  when: { command: SwipeOnDog, with: { swiperId: A, targetId: B } }
                  then:
                    - event: DogLiked
                      with: { swiperId: A }
          - name: DiscoveryFeed
            pattern: View
            readModels: [DiscoveryFeed]
        """;

    [Fact]
    public void a_valid_file_reads_clean()
    {
        var reading = CuratedModelReader.Read(Valid);

        reading.Problems.ShouldBeEmpty();
        reading.Succeeded.ShouldBeTrue();
        reading.File!.Model.ShouldBe("CritterCrush");
        reading.File.Slices.Count.ShouldBe(2);
        reading.File.Slices[0].Events.ShouldBe(["DogLiked", "DogPassed"]);
        reading.File.Slices[0].Specifications!.Scenarios.Single().When!.With["swiperId"].ShouldBe("A");
    }

    [Fact]
    public void a_missing_schema_is_named_as_probably_not_this_format()
    {
        var reading = CuratedModelReader.Read("model: X\nslices: []");

        reading.Succeeded.ShouldBeFalse();
        reading.Problems.Single().ShouldContain("schema must be 1");
    }

    [Fact]
    public void the_model_name_is_required_because_it_is_the_merge_key()
    {
        var reading = CuratedModelReader.Read("schema: 1\nslices: []");

        reading.Problems.Single().ShouldContain("merge key");
    }

    [Fact]
    public void duplicate_slice_names_are_rejected_because_slices_merge_by_name()
    {
        var reading = CuratedModelReader.Read(
            """
            schema: 1
            model: X
            slices:
              - name: A
              - name: A
            """);

        reading.Problems.Single().ShouldContain("more than once");
    }

    [Fact]
    public void an_unknown_pattern_lists_the_legal_values()
    {
        var reading = CuratedModelReader.Read(
            """
            schema: 1
            model: X
            slices:
              - name: A
                pattern: Widget
            """);

        reading.Problems.Single().ShouldContain("Command | View | Automation | Translation");
    }

    [Fact]
    public void enum_values_read_case_insensitively_like_the_wire_does()
    {
        var reading = CuratedModelReader.Read(
            """
            schema: 1
            model: X
            slices:
              - name: A
                pattern: command
                trigger: { kind: http }
            """);

        reading.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void a_then_entry_must_pick_exactly_one_outcome()
    {
        var reading = CuratedModelReader.Read(
            """
            schema: 1
            model: X
            slices:
              - name: A
                specifications:
                  scenarios:
                    - name: S
                      then:
                        - event: E
                          readModel: R
            """);

        reading.Problems.Single().ShouldContain("exactly one of event / readModel / validationFails");
    }

    [Fact]
    public void a_scenario_asserts_events_or_a_read_model_never_both()
    {
        var reading = CuratedModelReader.Read(
            """
            schema: 1
            model: X
            slices:
              - name: A
                specifications:
                  scenarios:
                    - name: S
                      then:
                        - event: E
                        - readModel: R
            """);

        reading.Problems.ShouldContain(x => x.Contains("never both"));
    }

    [Fact]
    public void garbage_reports_a_parse_problem_not_a_stack_trace()
    {
        var reading = CuratedModelReader.Read("{{{{");

        reading.Succeeded.ShouldBeFalse();
        reading.Problems.Single().ShouldContain("not parseable");
    }
}
