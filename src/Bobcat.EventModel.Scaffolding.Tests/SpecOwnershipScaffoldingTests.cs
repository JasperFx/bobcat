using Bobcat.EventModel;
using Bobcat.EventModel.Scaffolding;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// Issue #324 part 4: the scaffolder emits a <c>.feature</c>, a skeleton, or nothing — never both
/// a feature and a skeleton, which is the duplicate-identity collision the manifest exists to
/// prevent.
/// </summary>
public class SpecOwnershipScaffoldingTests
{
    private static CuratedModelFile model() => new()
    {
        Schema = 1,
        Model = "CritterCrush",
        Namespace = "CritterCrush",
        Slices =
        [
            new CuratedSlice
            {
                Name = "ConfirmAppointment",
                Pattern = "Command",
                Domain = "Scheduling",
                Command = "ConfirmAppointment",
                Aggregates = ["Appointment"],
                Events = ["AppointmentConfirmed"],
                Trigger = new CuratedTrigger { Kind = "Http", Label = "a coordinator confirms" },
                Specifications = new CuratedSpecifications
                {
                    Feature = "BookingAppointments",
                    Scenarios =
                    [
                        new CuratedScenario
                        {
                            Name = "a proposal is confirmed",
                            Given = [new CuratedGiven { Event = "AppointmentProposed" }],
                            When = new CuratedWhen { Command = "ConfirmAppointment", With = { ["AppointmentId"] = "{streamId}" } },
                            Then = [new CuratedThen { Event = "AppointmentConfirmed" }]
                        }
                    ]
                }
            },
            new CuratedSlice
            {
                Name = "ProposeHomeCheckAppointment",
                Pattern = "Automation",
                Domain = "Scheduling",
                Aggregates = ["Appointment"],
                Events = ["HomeCheckAppointmentProposed"],
                // The label IS the event type, which is what CritterCrush's real model does and what
                // SliceScaffolder.TriggerFor reads. Prose here would silently make the act a type
                // named after the sentence.
                Trigger = new CuratedTrigger { Kind = "MessageHandler", Label = "HomeCheckAssignmentAccepted" },
                Specifications = new CuratedSpecifications
                {
                    Feature = "BookingAppointments",
                    Scenarios =
                    [
                        new CuratedScenario
                        {
                            Name = "an accepted assignment proposes a visit",
                            When = new CuratedWhen { Command = "HomeCheckAssignmentAccepted" },
                            Then = [new CuratedThen { Event = "HomeCheckAppointmentProposed" }]
                        }
                    ]
                }
            }
        ]
    };

    private static SpecOwnershipPlan projected(string slice, string owner) => SpecOwnershipPlan.For(new SpecOwnershipFile
    {
        Schema = 1,
        Model = "CritterCrush",
        Slices = [new SpecOwnership { Slice = slice, Kind = "unit", Authoring = "projected", Owner = owner, CoveredBy = "x/y" }]
    });

    [Fact]
    public void with_no_manifest_nothing_changes()
    {
        var before = SliceScaffolder.ScaffoldFeatures(model(), arrangements: false);
        var after = SliceScaffolder.ScaffoldFeatures(model(), arrangements: false, SpecOwnershipPlan.None);

        after.Keys.ShouldBe(before.Keys);
        after["Features/BookingAppointments.feature"].ShouldBe(before["Features/BookingAppointments.feature"]);
    }

    [Fact]
    public void a_projected_slice_contributes_no_scenarios_to_the_feature()
    {
        var features = SliceScaffolder.ScaffoldFeatures(
            model(), arrangements: false, projected("ProposeHomeCheckAppointment", "CritterCrush.Specs.ProposalSpecs"));

        var feature = features["Features/BookingAppointments.feature"];

        feature.ShouldNotContain("@slice:ProposeHomeCheckAppointment");
        feature.ShouldNotContain("an accepted assignment proposes a visit");

        // And the sibling slice sharing the feature survives — the filter is per slice, because a
        // feature legally spans slices and dropping the file would take the sibling with it.
        feature.ShouldContain("@slice:ConfirmAppointment");
        feature.ShouldContain("a proposal is confirmed");
    }

    [Fact]
    public void a_feature_left_with_no_slices_writes_no_file_at_all()
    {
        var ownership = SpecOwnershipPlan.For(new SpecOwnershipFile
        {
            Schema = 1,
            Model = "CritterCrush",
            Slices =
            [
                new SpecOwnership { Slice = "ConfirmAppointment", Kind = "unit", Authoring = "projected", Owner = "S.A", CoveredBy = "x/y" },
                new SpecOwnership { Slice = "ProposeHomeCheckAppointment", Kind = "unit", Authoring = "projected", Owner = "S.A", CoveredBy = "x/y" }
            ]
        });

        SliceScaffolder.ScaffoldFeatures(model(), arrangements: false, ownership)
            .ShouldNotContainKey("Features/BookingAppointments.feature");
    }

    [Fact]
    public void a_projected_slice_gets_a_skeleton_bound_to_it()
    {
        var files = SpecSkeletons.Scaffold(
            model(), projected("ProposeHomeCheckAppointment", "CritterCrush.Specs.ProposalSpecs"));

        var skeleton = files["Specs/ProposalSpecs.cs"];

        skeleton.ShouldContain("namespace CritterCrush.Specs;");
        skeleton.ShouldContain("[BobcatFeature(\"BookingAppointments\")]");

        // An Automation has no type bearing the slice's name — its only type is {Slice}Handler,
        // which is precisely the type SliceType must never be given. So the string spelling.
        skeleton.ShouldContain("[BobcatSlice(SliceName = \"ProposeHomeCheckAppointment\")]");

        skeleton.ShouldContain("public void an_accepted_assignment_proposes_a_visit()");
        skeleton.ShouldContain("// When HomeCheckAssignmentAccepted");
        skeleton.ShouldContain("// Then HomeCheckAppointmentProposed is emitted");

        // Spec-first: it runs on day one and fails on its own slice rather than passing vacuously.
        skeleton.ShouldContain("throw new NotImplementedException");
    }

    [Fact]
    public void a_command_slice_binds_by_type_because_its_command_bears_the_slice_name()
    {
        var files = SpecSkeletons.Scaffold(
            model(), projected("ConfirmAppointment", "CritterCrush.Specs.BookingSpecs"));

        files["Specs/BookingSpecs.cs"].ShouldContain("[BobcatSlice(SliceType = typeof(ConfirmAppointment))]");
    }

    [Fact]
    public void a_view_slice_binds_by_its_read_model_type()
    {
        var view = new CuratedModelFile
        {
            Schema = 1,
            Model = "CritterCrush",
            Slices = [new CuratedSlice { Name = "AppointmentsQueue", Pattern = "View", ReadModels = ["AppointmentsQueue"] }]
        };

        SpecSkeletons.BindingFor(view.Slices[0]).ShouldBe("SliceType = typeof(AppointmentsQueue)");
    }

    [Fact]
    public void one_owner_covering_two_slices_binds_per_test_rather_than_per_class()
    {
        // The case part 1's method-level [BobcatSlice] exists for. A class-level binding would
        // claim one slice for both tests.
        var ownership = SpecOwnershipPlan.For(new SpecOwnershipFile
        {
            Schema = 1,
            Model = "CritterCrush",
            Slices =
            [
                new SpecOwnership { Slice = "ConfirmAppointment", Kind = "unit", Authoring = "projected", Owner = "CritterCrush.Specs.Booking", CoveredBy = "x/y" },
                new SpecOwnership { Slice = "ProposeHomeCheckAppointment", Kind = "unit", Authoring = "projected", Owner = "CritterCrush.Specs.Booking", CoveredBy = "x/y" }
            ]
        });

        var skeleton = SpecSkeletons.Scaffold(model(), ownership)["Specs/Booking.cs"];

        skeleton.ShouldContain("[BobcatSlice(SliceType = typeof(ConfirmAppointment))]");
        skeleton.ShouldContain("[BobcatSlice(SliceName = \"ProposeHomeCheckAppointment\")]");
        skeleton.ShouldContain("public void a_proposal_is_confirmed()");
        skeleton.ShouldContain("public void an_accepted_assignment_proposes_a_visit()");
    }

    [Fact]
    public void an_existing_suite_adopting_a_slice_gets_no_skeleton()
    {
        // integration + projected is the Marten DaemonTests row: the suite already exists, so the
        // whole behaviour is that no .feature is written.
        var ownership = SpecOwnershipPlan.For(new SpecOwnershipFile
        {
            Schema = 1,
            Model = "CritterCrush",
            Slices =
            [
                new SpecOwnership
                {
                    Slice = "ProposeHomeCheckAppointment", Kind = "integration", Authoring = "projected",
                    Owner = "Marten.DaemonTests.RebuildTests"
                }
            ]
        });

        SpecSkeletons.Scaffold(model(), ownership).ShouldBeEmpty();

        SliceScaffolder.ScaffoldFeatures(model(), arrangements: false, ownership)
            ["Features/BookingAppointments.feature"]
            .ShouldNotContain("@slice:ProposeHomeCheckAppointment");
    }

    [Fact]
    public void a_code_first_slice_gets_a_specification_skeleton_carrying_its_slice_tag()
    {
        var ownership = SpecOwnershipPlan.For(new SpecOwnershipFile
        {
            Schema = 1,
            Model = "CritterCrush",
            Slices =
            [
                new SpecOwnership { Slice = "ConfirmAppointment", Authoring = "code-first", Owner = "CritterCrush.Specs.BookingSpecs" }
            ]
        });

        var skeleton = SpecSkeletons.Scaffold(model(), ownership)["Specs/BookingSpecs.cs"];

        skeleton.ShouldContain("[FixtureTitle(\"BookingAppointments\")]");
        skeleton.ShouldContain("[Scenario(\"a proposal is confirmed\", Tags = [\"slice:ConfirmAppointment\", \"pattern:Command\", \"domain:Scheduling\"])]");
        skeleton.ShouldContain("throw new NotImplementedException");
    }

    [Fact]
    public void scaffold_all_writes_the_skeleton_and_no_feature_for_the_same_slice()
    {
        var files = SliceScaffolder.ScaffoldAll(
            model(), projected("ProposeHomeCheckAppointment", "CritterCrush.Specs.ProposalSpecs"));

        files.ShouldContainKey("Specs/ProposalSpecs.cs");
        files["Features/BookingAppointments.feature"].ShouldNotContain("@slice:ProposeHomeCheckAppointment");
    }

    [Fact]
    public void a_scenario_name_a_method_cannot_spell_is_flagged_in_the_skeleton_itself()
    {
        var punctuated = model();
        punctuated.Slices[1].Specifications!.Scenarios[0].Name = "an assignment, once accepted, books a visit";

        var skeleton = SpecSkeletons.Scaffold(
            punctuated, projected("ProposeHomeCheckAppointment", "CritterCrush.Specs.ProposalSpecs"))
            ["Specs/ProposalSpecs.cs"];

        skeleton.ShouldContain("publishes a DIFFERENT identity");
        skeleton.ShouldContain("public void an_assignment_once_accepted_books_a_visit()");
    }

    [Fact]
    public void the_skeleton_names_the_act_the_code_actually_takes_not_the_one_the_model_writes()
    {
        // An automation's curated `when:` names the SLICE, but the handler takes the trigger event
        // off the bus. Reading the model raw made the projected lane describe the act differently
        // from the .feature the Gherkin lane writes for the same scenario — issue #231's defect
        // (two halves deriving one decision separately) in a new place. Found by running the real
        // CritterCrush model through the scaffolder.
        var files = SpecSkeletons.Scaffold(
            model(), projected("ProposeHomeCheckAppointment", "CritterCrush.Specs.ProposalSpecs"));

        var skeleton = files["Specs/ProposalSpecs.cs"];

        skeleton.ShouldContain("// When HomeCheckAssignmentAccepted is received");
        skeleton.ShouldNotContain("// When ProposeHomeCheckAppointment");
    }

    [Fact]
    public void an_http_slice_names_its_route_because_that_is_what_the_emitted_code_answers_on()
    {
        var files = SpecSkeletons.Scaffold(
            model(), projected("ConfirmAppointment", "CritterCrush.Specs.BookingSpecs"));

        files["Specs/BookingSpecs.cs"].ShouldContain("is posted to \"/");
    }

    [Fact]
    public void a_collapsed_endpoint_refuses_with_a_400_and_not_with_a_thrown_validation_failure()
    {
        // The two refusal forms are not interchangeable, and emitting the wrong one is not a
        // compile error — it costs a permanently red scenario instead.
        var refusing = model();
        refusing.Slices[0].Specifications!.Scenarios.Add(new CuratedScenario
        {
            Name = "a cancelled appointment is not confirmed",
            When = new CuratedWhen { Command = "ConfirmAppointment" },
            Then = [new CuratedThen { ValidationFails = "the appointment was cancelled" }]
        });

        var skeleton = SpecSkeletons.Scaffold(
            refusing, projected("ConfirmAppointment", "CritterCrush.Specs.BookingSpecs"))
            ["Specs/BookingSpecs.cs"];

        skeleton.ShouldContain("// Then the response is 400");
        skeleton.ShouldNotContain("// Then validation fails with");
    }

    [Fact]
    public void a_skeleton_imports_the_namespace_of_every_type_it_binds()
    {
        // A scaffold always compiles (issue #226). `SliceType = typeof(ConfirmAppointment)` is a
        // type reference, and the slice's code lands in {namespace}.{domain} — a different
        // namespace from the specs' own — so without the using the file does not build, and one
        // unresolvable binding fails the whole spec project.
        //
        // Found by building the real CritterCrush against 0.25.0: CS0246 on AppointmentsQueue,
        // MyAppointments and VolunteerApplicationsQueue, every one of them a View slice bound by
        // type.
        var skeleton = SpecSkeletons.Scaffold(
            model(), projected("ConfirmAppointment", "CritterCrush.Specs.BookingSpecs"))
            ["Specs/BookingSpecs.cs"];

        skeleton.ShouldContain("using CritterCrush.Scheduling;");
        skeleton.ShouldContain("[BobcatSlice(SliceType = typeof(ConfirmAppointment))]");
    }

    [Fact]
    public void a_skeleton_binding_only_by_NAME_needs_no_import()
    {
        // An Automation binds by string, so there is no type reference to resolve and an unused
        // using would just be noise the author has to delete.
        var skeleton = SpecSkeletons.Scaffold(
            model(), projected("ProposeHomeCheckAppointment", "CritterCrush.Specs.ProposalSpecs"))
            ["Specs/ProposalSpecs.cs"];

        skeleton.ShouldContain("[BobcatSlice(SliceName = \"ProposeHomeCheckAppointment\")]");
        skeleton.ShouldNotContain("using CritterCrush.Scheduling;");
    }

    [Fact]
    public void a_method_name_round_trips_only_when_the_scenario_name_is_spellable()
    {
        ProjectedSpecNaming.RoundTrips("a proposal is confirmed").ShouldBeTrue();
        ProjectedSpecNaming.RoundTrips("a proposal is confirmed, twice").ShouldBeFalse();
        ProjectedSpecNaming.MethodNameFor("a proposal is confirmed").ShouldBe("a_proposal_is_confirmed");
        ProjectedSpecNaming.MethodNameFor("2 proposals").ShouldBe("_2_proposals");
    }
}
