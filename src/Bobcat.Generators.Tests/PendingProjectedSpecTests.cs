using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// Issue #404: a pending projected spec reaches the slice as a <c>PendingSpecification</c>
/// hotspot rather than as a specification — the same shape the Gherkin lane's step-less scenario
/// produces (jasperfx#689), so <c>SpecIdentityAudit</c> reads it as joined rather than as drift.
/// </summary>
public class PendingProjectedSpecTests
{
    private const string Preamble =
        """
        using System;
        using Xunit;

        public record ConfirmAppointment(Guid AppointmentId);

        """;

    [Fact]
    public void a_pending_spec_is_a_hotspot_and_not_a_specification()
    {
        var model = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking")]
            public class booking_specs
            {
                [BobcatSpec(typeof(ConfirmAppointment), Pending = true)]
                public void a_proposal_is_confirmed()
                {
                }
            }
            """).GeneratedSource("EventModel");

        model.ShouldContain(
            """HotspotDescriptor.PendingSpecification("Booking/a proposal is confirmed")""");

        // And NOT a specification, which is the half that matters: a declared-but-unanswered
        // question counted as evidence would make the slice look verified by a stub.
        model.ShouldNotContain(
            """SpecificationDescriptor("Booking/a proposal is confirmed",""");
    }

    [Fact]
    public void dropping_the_marker_turns_it_back_into_an_ordinary_specification()
    {
        // "Disappears on its own once the marker is removed" — there is no second place to update,
        // which is what keeps a stub from outliving its stub phase.
        var model = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking")]
            public class booking_specs
            {
                [BobcatSpec(typeof(ConfirmAppointment))]
                public void a_proposal_is_confirmed()
                {
                }
            }
            """).GeneratedSource("EventModel");

        model.ShouldContain(
            """SpecificationDescriptor("Booking/a proposal is confirmed",""");
        model.ShouldNotContain("PendingSpecification");
    }

    [Fact]
    public void a_projected_spec_with_no_steps_is_not_pending_by_itself()
    {
        // The rule the Gherkin lane uses deliberately does NOT carry over. There a step-less
        // scenario IS the pending case, because it says nothing. A projected test with no marker
        // comments says plenty — it runs real code and publishes a real verdict, and 33 of this
        // repository's own 41 projected samples declare no marker steps at all. Inferring pending
        // from "no steps" would have turned most of a working suite into open questions.
        var model = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking"), BobcatScenario]
            public class booking_specs
            {
                [Fact]
                public void a_proposal_is_confirmed()
                {
                }

                [BobcatSpec(typeof(ConfirmAppointment))]
                public void a_proposal_is_rejected()
                {
                }
            }
            """).GeneratedSource("EventModel");

        model.ShouldNotContain("PendingSpecification");
    }

    [Fact]
    public void pending_false_is_the_same_as_not_saying_it()
    {
        var model = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking")]
            public class booking_specs
            {
                [BobcatSpec(typeof(ConfirmAppointment), Pending = false)]
                public void a_proposal_is_confirmed()
                {
                }
            }
            """).GeneratedSource("EventModel");

        model.ShouldNotContain("PendingSpecification");
        model.ShouldContain("""SpecificationDescriptor("Booking/a proposal is confirmed",""");
    }

    [Fact]
    public void one_class_can_hold_a_pending_spec_beside_a_live_one()
    {
        // The shape stub-first work actually produces: some of the slice is specified, some is
        // still a question. Both identities land on the slice, in their own halves.
        var model = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking")]
            public class booking_specs
            {
                [BobcatSpec(typeof(ConfirmAppointment))]
                public void a_proposal_is_confirmed()
                {
                    // Given a proposed appointment
                }

                [BobcatSpec(typeof(ConfirmAppointment), Pending = true)]
                public void a_confirmed_appointment_cannot_be_confirmed_twice()
                {
                }
            }
            """).GeneratedSource("EventModel");

        model.ShouldContain("""SpecificationDescriptor("Booking/a proposal is confirmed",""");
        model.ShouldContain(
            "HotspotDescriptor.PendingSpecification("
            + "\"Booking/a confirmed appointment cannot be confirmed twice\")");
    }
}
