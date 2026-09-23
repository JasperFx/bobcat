using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// Issue #379: a marked spec class that never opens a recording renders as no specification at all,
/// and nothing about running it says so.
/// </summary>
/// <remarks>
/// <c>ScenarioRecorder.Step</c> is null-conditional on the ambient recording, so with no scenario
/// every step records into <c>NoStep.Instance</c>. A suite in that state passes, builds clean, and
/// publishes an empty canvas — and at run time it is indistinguishable from a suite that genuinely
/// has nothing to record. The compiler is the only place the difference is visible.
/// </remarks>
public class RecordingDiagnosticTests
{
    private const string Preamble =
        """
        using System;
        using Xunit;

        public record ConfirmAppointment(Guid AppointmentId);

        """;

    [Fact]
    public void a_marked_class_that_opens_no_recording_is_reported()
    {
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

        var warning = outcome.WithId("BOBCAT028").ShouldHaveSingleItem();
        warning.GetMessage().ShouldContain("booking_specs");
        warning.GetMessage().ShouldContain("[BobcatScenario]");

        // The message has to say what the COST is, not just what is missing. "Add an attribute" is
        // a chore; "none of its steps are recorded" is the reason anyone would.
        warning.GetMessage().ShouldContain("no specification at all");
    }

    [Fact]
    public void the_attribute_on_the_class_settles_it()
    {
        GeneratorHarness.Run(Preamble +
            """
            [BobcatScenario]
            [BobcatFeature("Booking")]
            [BobcatSlice(SliceType = typeof(ConfirmAppointment))]
            [BobcatSlice(SliceType = typeof(ConfirmAppointment))]
            public class booking_specs
            {
                [Fact]
                public void a_proposal_is_confirmed()
                {
                    // Given a proposed appointment
                }
            }
            """).WithId("BOBCAT028").ShouldBeEmpty();
    }

    [Fact]
    public void a_qualified_spelling_counts()
    {
        // Matched by short name on purpose: Bobcat.Xunit and Bobcat.TUnit ship the same attribute
        // under their own namespaces, and a suite may write either one qualified. Resolving the
        // symbol instead would fire on a compilation that references neither adapter — which is
        // precisely the compilation this diagnostic exists to warn.
        GeneratorHarness.Run(Preamble +
            """
            [Bobcat.Xunit.BobcatScenarioAttribute]
            [BobcatFeature("Booking")]
            [BobcatSlice(SliceType = typeof(ConfirmAppointment))]
            [BobcatSlice(SliceType = typeof(ConfirmAppointment))]
            public class booking_specs
            {
                [Fact]
                public void a_proposal_is_confirmed()
                {
                    // Given a proposed appointment
                }
            }
            """).WithId("BOBCAT028").ShouldBeEmpty();
    }

    [Fact]
    public void per_method_attributes_count_too()
    {
        // TUnit's adapter is a test-level receiver, so a suite may well carry it per method rather
        // than on the class. Either opens a recording; warning about the second would be wrong.
        GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Booking")]
            [BobcatSlice(SliceType = typeof(ConfirmAppointment))]
            public class booking_specs
            {
                [Fact]
                [BobcatScenario]
                public void a_proposal_is_confirmed()
                {
                    // Given a proposed appointment
                }
            }
            """).WithId("BOBCAT028").ShouldBeEmpty();
    }

    [Fact]
    public void an_unmarked_class_is_not_this_diagnostics_business()
    {
        // No [BobcatFeature] means the class never claimed to be a specification. Warning here
        // would fire on every ordinary test in the assembly.
        GeneratorHarness.Run(Preamble +
            """
            public class ordinary_tests
            {
                [Fact]
                public void a_proposal_is_confirmed()
                {
                    // Given a proposed appointment
                }
            }
            """).WithId("BOBCAT028").ShouldBeEmpty();
    }

    [Fact]
    public void a_feature_that_binds_no_slice_is_left_alone()
    {
        // [BobcatFeature] on its own is a legitimate compile-time use: Bobcat's own acceptance
        // tests carry it so the generator emits their declared steps, then assert on DeclaredSteps
        // without ever running a scenario. The first version of this diagnostic fired on four of
        // them — measured, not guessed, by building this repository with it on — which is what
        // narrowed the rule to [BobcatSlice]. Binding a slice is the claim that cannot be true
        // while nothing records: it says this behaviour is specified HERE, on the Event Model.
        GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Marker comments")]
            public class declared_step_tests
            {
                [Fact]
                public void a_proposal_is_confirmed()
                {
                    // Given a proposed appointment
                }
            }
            """).WithId("BOBCAT028").ShouldBeEmpty();
    }
}
