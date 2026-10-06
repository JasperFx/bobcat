using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// Issue #403, the generator half. <c>[BobcatSpec]</c> does three jobs the generator looks for by
/// three separate simple-name checks — "is this a test method", "does anything open a recording",
/// "what slice does this bind" — and inheritance tells the generator nothing, because it never
/// sees the base type. Each of those checks is a fact to record, and this is where they are.
/// </summary>
public class BobcatSpecBindingTests
{
    private const string Preamble =
        """
        using System;
        using Xunit;

        public record ConfirmAppointment(Guid AppointmentId);
        public record RequestReschedule(Guid AppointmentId);

        """;

    [Fact]
    public void the_one_attribute_form_binds_a_slice_and_opens_a_recording()
    {
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking")]
            public class booking_specs
            {
                [BobcatSpec(typeof(ConfirmAppointment))]
                public void a_proposal_is_confirmed()
                {
                    // Given a proposed appointment
                }
            }
            """);

        // No BOBCAT028. The trap the issue exists to remove: [BobcatSpec] implies
        // [BobcatScenario], so the attribute that CLAIMS the slice is the attribute that OPENS the
        // recording and the two can no longer come apart. Before the name check knew about it,
        // this exact source warned that nothing opened a scenario — about a suite that records
        // perfectly well.
        outcome.WithId("BOBCAT028").ShouldBeEmpty();

        // And the slice is bound, from the POSITIONAL typeof — which is the ergonomic point.
        outcome.WithId("BOBCAT023").ShouldBeEmpty();
        outcome.GeneratedSource("EventModel").ShouldContain("ConfirmAppointment");
    }

    [Fact]
    public void the_method_is_recognised_as_a_test_without_a_fact_attribute_beside_it()
    {
        // [BobcatSpec] IS a FactAttribute subclass, so xUnit discovers it — but the generator
        // matches by name and would otherwise skip the method entirely, producing a spec class
        // with no scenarios and therefore no specification at all.
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking")]
            [BobcatSlice(SliceType = typeof(ConfirmAppointment))]
            public class booking_specs
            {
                [BobcatSpec]
                public void a_proposal_is_confirmed()
                {
                    // Given a proposed appointment
                    // When it is confirmed
                }
            }
            """);

        var source = outcome.GeneratedSource("DeclaredSteps");
        source.ShouldContain("a proposal is confirmed");
        source.ShouldContain("a proposed appointment");
    }

    [Fact]
    public void the_named_settings_work_the_same_way_as_on_bobcat_slice()
    {
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking")]
            public class booking_specs
            {
                [BobcatSpec(SliceName = "ProposeHomeCheck", Domain = "Scheduling",
                            Chapter = "BookingAppointments", Pattern = "Automation")]
                public void an_accepted_assignment_proposes_a_visit()
                {
                    // Given an accepted assignment
                }
            }
            """);

        var model = outcome.GeneratedSource("EventModel");
        model.ShouldContain("ProposeHomeCheck");
        model.ShouldContain("Scheduling");
        model.ShouldContain("BookingAppointments");
        model.ShouldContain("Automation");
    }

    [Fact]
    public void both_spellings_naming_different_slices_is_still_bobcat023_across_the_two_attributes()
    {
        // The two attributes are the same settings under two spellings, so a method carrying both
        // is one binding stated twice — and the existing rule already says what to do when two
        // spellings disagree. Preferring one would make the loser's slice vanish silently, which
        // is the outcome BOBCAT023 was written to prevent in the first place.
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking")]
            public class booking_specs
            {
                [BobcatSpec(typeof(ConfirmAppointment))]
                [BobcatSlice(SliceName = "RequestReschedule")]
                public void a_proposal_is_confirmed()
                {
                    // Given a proposed appointment
                }
            }
            """);

        var error = outcome.WithId("BOBCAT023").ShouldHaveSingleItem();
        error.GetMessage().ShouldContain("RequestReschedule");
        error.GetMessage().ShouldContain("ConfirmAppointment");
    }

    [Fact]
    public void the_same_slice_from_both_attributes_is_fine()
    {
        // Redundant, not wrong — the same latitude [BobcatSlice] already gives someone being
        // explicit with SliceName and SliceType together.
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking")]
            [BobcatSlice(SliceType = typeof(ConfirmAppointment))]
            public class booking_specs
            {
                [BobcatSpec(typeof(ConfirmAppointment))]
                public void a_proposal_is_confirmed()
                {
                    // Given a proposed appointment
                }
            }
            """);

        outcome.WithId("BOBCAT023").ShouldBeEmpty();
    }

    [Fact]
    public void bobcat024_names_the_attribute_it_actually_fired_on()
    {
        // Semantics unchanged (same condition, same severity) — but a message that said
        // "[BobcatSlice(...)]" about a [BobcatSpec] would send the reader to the wrong line.
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking")]
            public class booking_specs
            {
                [BobcatSpec(SliceName = "ConfirmAppointment")]
                public void a_proposal_is_confirmed()
                {
                    // Given a proposed appointment
                }
            }
            """);

        outcome.WithId("BOBCAT024").ShouldHaveSingleItem()
            .GetMessage().ShouldContain("[BobcatSpec(SliceName = \"ConfirmAppointment\")]");
    }

    [Fact]
    public void a_class_that_binds_a_slice_with_a_plain_fact_is_still_reported()
    {
        // The counterweight. BOBCAT028 has to keep firing on the shape it was written for, or
        // teaching the name check about [BobcatSpec] would have quietly switched it off.
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking")]
            [BobcatSlice(SliceType = typeof(ConfirmAppointment))]
            public class booking_specs
            {
                [Fact]
                public void a_proposal_is_confirmed()
                {
                    // Given a proposed appointment
                }
            }
            """);

        outcome.WithId("BOBCAT028").ShouldHaveSingleItem();
    }
}
