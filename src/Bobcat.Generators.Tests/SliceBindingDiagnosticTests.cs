using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// Issue #324: the diagnostics that make "types where they exist, strings where they do not" a rule
/// the build enforces rather than a convention to remember.
/// </summary>
public class SliceBindingDiagnosticTests
{
    private const string Preamble =
        """
        using System;
        using Xunit;

        public record ConfirmAppointment(Guid AppointmentId);

        """;

    [Fact]
    public void both_spellings_set_to_different_slices_is_an_error()
    {
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking")]
            [BobcatSlice(SliceName = "RequestReschedule", SliceType = typeof(ConfirmAppointment))]
            public class booking_specs
            {
                [Fact]
                public void a_proposal_is_confirmed()
                {
                    // Given a proposed appointment
                }
            }
            """);

        var error = outcome.WithId("BOBCAT023").ShouldHaveSingleItem();
        error.GetMessage().ShouldContain("RequestReschedule");
        error.GetMessage().ShouldContain("ConfirmAppointment");
        error.GetMessage().ShouldContain("SliceType means exactly SliceName = type.Name");
    }

    [Fact]
    public void the_same_slice_by_both_spellings_is_fine()
    {
        // Redundant, not wrong. Only a DISAGREEMENT is an error — otherwise the rule would punish
        // someone being explicit.
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking")]
            [BobcatSlice(SliceName = "ConfirmAppointment", SliceType = typeof(ConfirmAppointment))]
            public class booking_specs
            {
                [Fact]
                public void a_proposal_is_confirmed()
                {
                    // Given a proposed appointment
                }
            }
            """);

        outcome.WithId("BOBCAT023").ShouldBeEmpty();
    }

    [Fact]
    public void a_literal_name_that_is_a_type_in_this_compilation_is_nudged_toward_the_type()
    {
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking")]
            [BobcatSlice(SliceName = "ConfirmAppointment")]
            public class booking_specs
            {
                [Fact]
                public void a_proposal_is_confirmed()
                {
                    // Given a proposed appointment
                }
            }
            """);

        var warning = outcome.WithId("BOBCAT024").ShouldHaveSingleItem();
        warning.GetMessage().ShouldContain("typeof(ConfirmAppointment)");
    }

    [Fact]
    public void a_literal_name_with_no_such_type_is_left_alone()
    {
        // Every Automation slice is this case — its handler is {Slice}Handler and there is no
        // command type at all — so nudging here would be noise nobody can act on.
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking")]
            [BobcatSlice(SliceName = "ProposeHomeCheckAppointment")]
            public class booking_specs
            {
                [Fact]
                public void an_assignment_proposes_a_visit()
                {
                    // Given an accepted assignment
                }
            }
            """);

        outcome.WithId("BOBCAT024").ShouldBeEmpty();
        outcome.WithId("BOBCAT023").ShouldBeEmpty();
    }
}
