using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// bobcat#444: every command gets an aggregate, declared or inferred from the examples' lineage,
/// and every inference is called out. A command that does not only start a stream needs a DCB
/// decider or one or more single-stream aggregates, so aggregate-less is never the default.
/// </summary>
public class AggregateInferenceTests
{
    // The shape of K9CRUSH's two chapters, cut to what the inference reads: a stream started and
    // then decided against, a second stream whose starters only share its name, and a decision
    // drawing on two streams
    private const string Shelter =
        """
        slices:
          Volunteering:
            steps:
              - c: Volunteer / Apply to volunteer
              - e: Volunteer / Volunteer application submitted
              - c: Admin / Review volunteer application
              - e: Admin / Volunteer application reviewed
              - c: Admin / Approve volunteer
              - e: Admin / Volunteer approved
              - c: Admin / Reject volunteer application
              - e: Admin / Volunteer application rejected
              - c: Staff / Request home check
              - e: Staff / Home check requested
              - c: Volunteer / Accept home check assignment
              - e: Volunteer / Home check assignment accepted
            tests:
              Submitted:
                when:
                  - c: Apply to volunteer
                then:
                  - e: Volunteer application submitted
              Reviewed:
                given:
                  - e: Volunteer application submitted
                when:
                  - c: Review volunteer application
                then:
                  - e: Volunteer application reviewed
              Approved:
                given:
                  - e: Volunteer application reviewed
                when:
                  - c: Approve volunteer
                then:
                  - e: Volunteer approved
              Rejected:
                given:
                  - e: Volunteer application reviewed
                when:
                  - c: Reject volunteer application
                then:
                  - e: Volunteer application rejected
              Requested:
                given:
                  - e: Application reviewed
                when:
                  - c: Request home check
                then:
                  - e: Home check requested
              Accepted:
                given:
                  - e: Home check requested
                  - e: Volunteer approved
                when:
                  - c: Accept home check assignment
                then:
                  - e: Home check assignment accepted
          Booking:
            steps:
              - c: System / Propose home check appointment
              - e: System / Home check appointment proposed
              - c: System / Propose foster handover appointment
              - e: System / Foster handover appointment proposed
              - c: Member / Confirm appointment
              - e: Member / Appointment confirmed
              - c: Staff / Cancel appointment
              - e: Staff / Appointment cancelled
            tests:
              Home check proposed:
                when:
                  - c: Propose home check appointment
                then:
                  - e: Home check appointment proposed
              Foster proposed:
                when:
                  - c: Propose foster handover appointment
                then:
                  - e: Foster handover appointment proposed
              Confirmed:
                given:
                  - e: Home check appointment proposed
                when:
                  - c: Confirm appointment
                then:
                  - e: Appointment confirmed
              Cancelled:
                given:
                  - e: Appointment confirmed
                when:
                  - c: Cancel appointment
                then:
                  - e: Appointment cancelled
        """;

    private static EmlangImportResult import(string yaml, params string[] overrides)
        => EmlangImport.ToCurated(EmlangReader.Read(yaml), "Shelter", "Shelter",
            overrides.Select(AggregateOverride.Parse).ToList());

    private static CuratedSlice slice(EmlangImportResult result, string name) => result.Model.Slices.Single(x => x.Name == name);

    [Fact]
    public void examples_group_the_events_into_streams_named_for_what_most_of_them_are_about()
    {
        var streams = import(Shelter).Model.EventStreams;

        foreach (var name in new[] { "VolunteerApplicationSubmitted", "VolunteerApplicationReviewed", "VolunteerApproved", "VolunteerApplicationRejected" })
        {
            streams[name].ShouldBe("VolunteerApplication");
        }

        foreach (var name in new[] { "HomeCheckRequested", "HomeCheckAssignmentAccepted" })
        {
            streams[name].ShouldBe("HomeCheck");
        }

        foreach (var name in new[] { "HomeCheckAppointmentProposed", "AppointmentConfirmed", "AppointmentCancelled" })
        {
            streams[name].ShouldBe("Appointment");
        }
    }

    [Fact]
    public void a_swimlane_never_names_the_stream()
    {
        var aggregates = import(Shelter).Model.Slices.SelectMany(CSharpModelWriter.AggregatesOf).ToList();

        aggregates.ShouldNotContain("Admin");
        aggregates.ShouldNotContain("Volunteer");
        aggregates.ShouldNotContain("Staff");
        aggregates.ShouldNotContain("System");
    }

    [Fact]
    public void a_slice_no_example_gives_an_earlier_event_of_its_stream_starts_it()
    {
        var result = import(Shelter);

        slice(result, "ApplyToVolunteer").StartsStream.ShouldBe("VolunteerApplication");
        slice(result, "ApplyToVolunteer").Aggregates.ShouldBeEmpty();
        slice(result, "ProposeHomeCheckAppointment").StartsStream.ShouldBe("Appointment");

        slice(result, "ReviewVolunteerApplication").StartsStream.ShouldBeNull();
        slice(result, "ReviewVolunteerApplication").Aggregates.ShouldBe(["VolunteerApplication"]);
        slice(result, "CancelAppointment").Aggregates.ShouldBe(["Appointment"]);
    }

    [Fact]
    public void a_stream_no_example_links_joins_the_one_its_subject_ends_with_and_says_it_was_by_name_only()
    {
        var result = import(Shelter);

        result.Model.EventStreams["FosterHandoverAppointmentProposed"].ShouldBe("Appointment");
        slice(result, "ProposeFosterHandoverAppointment").StartsStream.ShouldBe("Appointment");
        result.Report.ShouldContain(x => x.Contains("FosterHandoverAppointmentProposed joined the Appointment stream by name only"));
    }

    [Fact]
    public void an_example_of_one_stream_joins_what_it_expects_even_when_the_names_differ_and_says_so()
    {
        // `Application reviewed` → `Home check requested` share no subject, but the example is about
        // one stream, so what it expects is appended there (ItemAdded on the Order)
        var result = import(Shelter);

        result.Model.EventStreams["ApplicationReviewed"].ShouldBe("HomeCheck");
        slice(result, "RequestHomeCheck").Aggregates.ShouldBe(["HomeCheck"]);
        result.Report.ShouldContain(x => x.Contains("HomeCheckRequested is on the same stream as ApplicationReviewed"));
    }

    [Fact]
    public void a_decision_drawing_on_two_streams_decides_against_both_and_is_told_to_choose()
    {
        var result = import(Shelter);
        var accept = slice(result, "AcceptHomeCheckAssignment");

        // The stream it appends to first, then the one it only reads
        accept.Aggregates.ShouldBe(["HomeCheck", "VolunteerApplication"]);
        accept.Callouts.ShouldContain(x => x.Contains("draws on several streams (HomeCheck, VolunteerApplication)") && x.Contains("DCB decider") && x.Contains("bobcat#443"));
    }

    [Fact]
    public void a_command_drawing_on_two_streams_carries_each_ones_identity_and_the_spec_sets_both()
    {
        var board = EmlangReader.Read(Shelter);
        var model = EmlangImport.ToCurated(board, "Shelter", "Shelter").Model;
        var specs = EmlangSpecWriter.Write(board, model, "Shelter");
        var stubs = CSharpModelWriter.Write(model, "Shelter", specs.Additions).AllStubs();
        var code = specs.AllCode();

        stubs.ShouldContain("public record AcceptHomeCheckAssignment(Guid HomeCheckId, Guid VolunteerApplicationId);");
        code.ShouldContain("await GivenEvents<HomeCheck>(theHomeCheck, Specify<HomeCheckRequested>());");
        code.ShouldContain("await GivenEventsOn<VolunteerApplication>(theVolunteerApplication, Specify<VolunteerApproved>());");
        code.ShouldContain("await WhenReceived(Specify<AcceptHomeCheckAssignment>().With(x => x.HomeCheckId, theHomeCheck).With(x => x.VolunteerApplicationId, theVolunteerApplication));");
    }

    [Fact]
    public void the_definition_starts_and_decides_against_the_inferred_aggregates_and_calls_out_every_one()
    {
        var model = import(Shelter).Model;
        var definition = CSharpModelWriter.Write(model, "Shelter").Definition;

        definition.ShouldContain("model.Aggregate<VolunteerApplication>();");
        definition.ShouldContain(".StartsStream<VolunteerApplication>()");
        definition.ShouldContain(".Against<Appointment>()");
        definition.ShouldContain("// ⚠ inferred: starts the VolunteerApplication stream — no example gives it an earlier VolunteerApplication event");
        definition.ShouldContain("// ⚠ inferred: decides against VolunteerApplication — its examples give VolunteerApplication events before it appends");
    }

    [Fact]
    public void the_specs_arrange_on_the_typed_aggregate_and_address_it_by_id()
    {
        var board = EmlangReader.Read(Shelter);
        var code = EmlangSpecWriter.Write(board, EmlangImport.ToCurated(board, "Shelter", "Shelter").Model, "Shelter").AllCode();

        code.ShouldContain("await GivenEvents<Appointment>(theAppointment, Specify<HomeCheckAppointmentProposed>());");
        code.ShouldContain("await WhenReceived(Specify<ConfirmAppointment>().With(x => x.Id, theAppointment));");
        code.ShouldNotContain("await GivenEvents(the");
    }

    [Fact]
    public void an_override_wins_over_the_inference_and_is_not_called_out()
    {
        var result = import(Shelter, "ConfirmAppointment=Booking", "AcceptHomeCheckAssignment=HomeCheck");

        var confirm = slice(result, "ConfirmAppointment");
        confirm.Aggregates.ShouldBe(["Booking"]);
        confirm.Inferred.ShouldBeEmpty();
        confirm.Callouts.ShouldBeEmpty();
        result.Model.EventStreams["AppointmentConfirmed"].ShouldBe("Booking");

        // Overridden to one stream: no longer told to choose
        var accept = slice(result, "AcceptHomeCheckAssignment");
        accept.Aggregates.ShouldBe(["HomeCheck"]);
        accept.Callouts.ShouldBeEmpty();
    }

    [Fact]
    public void an_override_naming_no_slice_is_reported()
    {
        import(Shelter, "Nope=Order").Report.ShouldContain(x => x.Contains("no slice is named 'Nope'"));
    }

    [Fact]
    public void an_override_that_is_not_slice_equals_type_is_refused()
    {
        Should.Throw<FormatException>(() => AggregateOverride.Parse("ConfirmAppointment"));
    }

    [Fact]
    public void a_declared_aggregate_is_used_as_is_and_never_called_out()
    {
        var board = EventModelersJsonReader.Read(
            """
            { "slices": [ { "title": "Open", "aggregates": ["Cart"],
                "commands": [ { "title": "Open Cart", "aggregate": "Cart", "fields": [] } ],
                "events": [ { "title": "Cart Opened", "aggregate": "Cart", "fields": [] } ],
                "specifications": [ { "title": "Opens", "given": [],
                  "when": [ { "title": "Open Cart", "type": "COMMAND", "fields": [] } ],
                  "then": [ { "title": "Cart Opened", "type": "EVENT", "fields": [] } ] } ] } ] }
            """);
        var result = EmlangImport.ToCurated(board, "Carts");

        var open = result.Model.Slices.Single(x => x.Command == "OpenCart");
        open.StartsStream.ShouldBe("Cart");
        open.Inferred.ShouldBeEmpty();
        open.Callouts.ShouldBeEmpty();
        result.Report.ShouldNotContain(x => x.Contains("inferred aggregate Cart"));
    }

    [Fact]
    public void a_command_with_no_aggregate_is_reported_missing_never_made_aggregate_less()
    {
        var result = import(
            """
            slices:
              Ping:
                steps:
                  - c: Ping
            """);

        var ping = slice(result, "Ping");
        ping.Aggregates.ShouldBeEmpty();
        ping.StartsStream.ShouldBeNull();
        ping.Callouts.ShouldContain(x => x.StartsWith("⚠ missing: no aggregate. TODO:"));
        result.Report.ShouldContain(x => x.Contains("slice 'Ping': missing: no aggregate"));
        CSharpModelWriter.Write(result.Model, "Shelter").Definition.ShouldContain("// ⚠ missing: no aggregate. TODO:");
    }

    [Fact]
    public void a_type_only_an_example_names_lands_in_the_chapter_of_the_slice_whose_example_names_it()
    {
        var board = EmlangReader.Read(Shelter);
        var model = EmlangImport.ToCurated(board, "Shelter", "Shelter").Model;
        var specs = EmlangSpecWriter.Write(board, model, "Shelter");
        var output = CSharpModelWriter.Write(model, "Shelter", specs.Additions);

        output.Layout.FileOf("ApplicationReviewed").ShouldBe("Features/Volunteering/ApplicationReviewed.cs");
        output.Layout.NamespaceOf("ApplicationReviewed").ShouldBe("Shelter.Volunteering");
    }

    [Fact]
    public void the_inference_is_deterministic()
    {
        var first = import(Shelter);
        var second = import(Shelter);

        second.Report.ShouldBe(first.Report);
        second.Model.EventStreams.OrderBy(x => x.Key).ShouldBe(first.Model.EventStreams.OrderBy(x => x.Key));
    }

    [Fact]
    public void the_generated_definition_and_stubs_compile_into_one_model()
    {
        var board = EmlangReader.Read(Shelter);
        var model = EmlangImport.ToCurated(board, "Shelter", "Shelter").Model;
        var specs = EmlangSpecWriter.Write(board, model, "Shelter");
        var output = CSharpModelWriter.Write(model, "Shelter", specs.Additions);

        // One aggregate class per stream, in its chapter, every one stubbed once
        output.AllStubs().ShouldContain("public class VolunteerApplication { public Guid Id { get; set; } }");
        output.AllStubs().ShouldContain("public class HomeCheck { public Guid Id { get; set; } }");
        output.AllStubs().ShouldContain("public class Appointment { public Guid Id { get; set; } }");
        output.Layout.NamespaceOf("Appointment").ShouldBe("Shelter.Booking");

        CSharpModelWriterTests.compile(output).ShouldBeEmpty();

        // And what it declares is what was inferred: the descriptor carries the started stream and
        // both aggregates of the two-stream decision
        var descriptor = CSharpModelWriterTests.build(output);
        descriptor.Slices.Single(x => x.Name == "ApplyToVolunteer").StartsStream!.Name.ShouldBe("VolunteerApplication");
        descriptor.Slices.Single(x => x.Name == "AcceptHomeCheckAssignment").AggregateTypes.Select(x => x.Name)
            .ShouldBe(["HomeCheck", "VolunteerApplication"]);
    }
}
