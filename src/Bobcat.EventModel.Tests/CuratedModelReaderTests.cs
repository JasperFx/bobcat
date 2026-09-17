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

        reading.Problems.Single().ShouldContain("exactly one of event / readModel / validationFails / refusedWith");
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

    // --- Issue #337: a refusal that states the status it answers with ---

    private const string HttpSlice =
        """
        schema: 1
        model: X
        slices:
          - name: A
            pattern: Command
            trigger: { kind: Http }
            specifications:
              scenarios:
                - name: S
                  then:
                    - refusedWith: { status: STATUS, reason: REASON }
        """;

    private static CuratedModelReading readRefusal(string status, string reason)
        => CuratedModelReader.Read(HttpSlice.Replace("STATUS", status).Replace("REASON", reason));

    [Fact]
    public void a_stated_refusal_on_an_http_slice_is_well_formed()
    {
        var reading = readRefusal("409", "\"already cancelled\"");

        reading.Problems.ShouldBeEmpty();
        var refusal = reading.File!.Slices.Single().Specifications!.Scenarios.Single().Then.Single().RefusedWith;
        refusal!.Status.ShouldBe(409);
        refusal.Reason.ShouldBe("already cancelled");
    }

    [Fact]
    public void a_status_outside_4xx_and_5xx_is_not_a_refusal()
    {
        readRefusal("204", "\"fine\"").Problems.ShouldContain(x => x.Contains("a 4xx or a 5xx"));

        // A missing `status:` deserializes as 0, and reads as the same problem rather than as 400:
        // omitting the whole node is how a file says 400, by writing validationFails: instead.
        CuratedModelReader.Read(
            """
            schema: 1
            model: X
            slices:
              - name: A
                pattern: Command
                trigger: { kind: Http }
                specifications:
                  scenarios:
                    - name: S
                      then:
                        - refusedWith: { reason: "no status" }
            """).Problems.ShouldContain(x => x.Contains("reads as 0"));
    }

    [Fact]
    public void a_refusal_with_no_reason_scaffolds_nothing_anyone_can_act_on()
    {
        CuratedModelReader.Read(
            """
            schema: 1
            model: X
            slices:
              - name: A
                pattern: Command
                trigger: { kind: Http }
                specifications:
                  scenarios:
                    - name: S
                      then:
                        - refusedWith: { status: 403 }
            """).Problems.ShouldContain(x => x.Contains("`refusedWith.reason:` is required"));
    }

    [Fact]
    public void a_status_has_nowhere_to_be_asserted_off_the_http_lane()
    {
        // A bus-dispatched slice refuses by THROWING, which `Then validation fails with "…"`
        // asserts. Accepting a status there and dropping it is the silent degradation #337 is
        // about; the file is named as wrong instead.
        var reading = CuratedModelReader.Read(
            """
            schema: 1
            model: X
            slices:
              - name: A
                pattern: Command
                trigger: { kind: Scheduled }
                specifications:
                  scenarios:
                    - name: S
                      then:
                        - refusedWith: { status: 409, reason: "already cancelled" }
            """);

        reading.Problems.ShouldContain(x => x.Contains("does not answer over HTTP"));
        reading.Problems.ShouldContain(x => x.Contains("validationFails"));
    }

    [Fact]
    public void the_http_lane_is_case_insensitive_like_every_other_enum_field()
    {
        // `pattern: command` maps to a Command slice and validates; the predicate this shares
        // with the scaffolder used to compare ordinally, so the same file scaffolded as a bus
        // handler. A refusal must not be refused for the file's capitalization.
        CuratedModelReader.Read(
            """
            schema: 1
            model: X
            slices:
              - name: A
                pattern: command
                trigger: { kind: http }
                specifications:
                  scenarios:
                    - name: S
                      then:
                        - refusedWith: { status: 404, reason: "no such thing" }
            """).Problems.ShouldBeEmpty();
    }

    [Fact]
    public void garbage_reports_a_parse_problem_not_a_stack_trace()
    {
        var reading = CuratedModelReader.Read("{{{{");

        reading.Succeeded.ShouldBeFalse();
        reading.Problems.Single().ShouldContain("not parseable");
    }
}

/// <summary>
/// Issue #318: an unrecognised <c>fields:</c> type is reported, not silently turned into a string.
/// </summary>
/// <remarks>
/// A declaration names a type; a scenario value is a sample. The <c>string</c> fallback is right
/// for the second and wrong for the first, and the silence was the bug —
/// <c>statuses: Dictionary&lt;Guid, string&gt;</c> came out as <c>public string Statuses</c> with
/// nothing anywhere saying the model and the code had parted company.
/// </remarks>
public class UnrecognizedFieldTypeTests
{
    private const string Yaml =
        """
        schema: 1
        model: CritterCrush
        slices:
          - name: AppointmentsQueue
            pattern: View
            readModels: [AppointmentsQueue]
            elements:
              AppointmentsQueue:
                fields:
                  shelterId: Guid
                  statuses: Dictionary<Guid, string>
                  awaitingConfirmation: int
            specifications:
              feature: AppointmentsQueue
              scenarios:
                - name: The queue counts what is waiting
                  then:
                    - readModel: AppointmentsQueue
                      contains: { AwaitingConfirmation: "1", Kind: HomeCheck }
        """;

    private static CuratedModelReading reading() => CuratedModelReader.Read(Yaml);

    [Fact]
    public void an_unrecognized_declared_type_is_warned_about_but_still_loads()
    {
        var read = reading();

        // A warning, not a problem: models relying on the fallback exist and must keep loading.
        read.Succeeded.ShouldBeTrue();
        read.Problems.ShouldBeEmpty();

        var warning = read.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("slice 'AppointmentsQueue'");
        warning.ShouldContain("field 'statuses'");
        warning.ShouldContain("Dictionary<Guid, string>");
        warning.ShouldContain("emitted as `string`");
    }

    [Fact]
    public void a_scenario_value_is_a_sample_and_is_never_warned_about()
    {
        // `Kind: HomeCheck` is a sample value that infers as string, which is correct. Only the
        // ONE declared field is warned about — if scenario values were checked too, this model
        // would report several and the signal would be worthless.
        reading().Warnings.Count.ShouldBe(1);
    }

    [Fact]
    public void every_known_type_is_recognized_in_any_casing()
    {
        foreach (var known in CuratedFieldTypes.Known)
        {
            CuratedFieldTypes.TryInfer(known, out var same).ShouldBeTrue(known);
            same.ShouldBe(known);

            // Canonicalised rather than echoed: a field declared `GUID` used to be emitted
            // verbatim as `public GUID Foo`, which does not compile.
            CuratedFieldTypes.TryInfer(known.ToUpperInvariant(), out var shouted).ShouldBeTrue(known);
            shouted.ShouldBe(known);
        }
    }

    [Fact]
    public void the_stream_id_token_is_a_guid_and_not_a_sample_string()
    {
        CuratedFieldTypes.TryInfer(CuratedFieldTypes.StreamIdToken, out var type).ShouldBeTrue();
        type.ShouldBe("Guid");
    }
}
