using System.Reflection;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>bobcat#449: each [BobcatFeature] test and the command or slice it exercises, as JasperFx bindings.</summary>
public class SpecificationManifestTests
{
    private const string Specs =
        """
        using System;
        using System.Threading.Tasks;
        using Bobcat;
        using Xunit;
        using static Bobcat.Specifications;

        namespace Shelter.VolunteeringAndHomeChecks
        {
            public record ReviewVolunteerApplication(Guid Id);
            public record ConfirmAppointment(Guid AppointmentId);
            public class VolunteerApplicationsQueue { public Guid Id { get; set; } }
        }

        namespace Shelter.Specs
        {
            using Shelter.VolunteeringAndHomeChecks;

            public abstract class SpecBase
            {
                protected Task WhenReceived(object message) => Task.CompletedTask;
            }

            [BobcatFeature("ReviewVolunteerApplication")]
            public class review_volunteer_application : SpecBase
            {
                [Fact]
                public async Task volunteer_application_reviewed()
                {
                    await WhenReceived(Specify<ReviewVolunteerApplication>().With(x => x.Id, Guid.Empty));
                }

                [Fact]
                public async Task reviewed_from_a_new_record()
                {
                    await WhenReceived(new ReviewVolunteerApplication(Guid.Empty));
                }

                public void not_a_test() { }
            }

            [BobcatFeature("VolunteerApplicationsQueue")]
            [BobcatSlice(SliceType = typeof(VolunteerApplicationsQueue), Domain = "Volunteering")]
            public class volunteer_applications_queue_feature : SpecBase
            {
                [Fact]
                public async Task volunteer_applications_queue() { await Task.CompletedTask; }

                [Fact]
                [BobcatSlice(SliceName = "ConfirmAppointment")]
                public async Task rebound_to_another_slice()
                {
                    await WhenReceived(Specify<ConfirmAppointment>());
                }
            }

            // No [BobcatFeature]: not a Bobcat specification, so not in the manifest
            public class plain_tests : SpecBase
            {
                [Fact]
                public async Task ignored() => await WhenReceived(new ConfirmAppointment(Guid.Empty));
            }
        }
        """;

    private static IReadOnlyList<SpecificationBindingDescriptor> bindings()
    {
        var outcome = GeneratorHarness.Run(Specs);
        outcome.CompilationErrors.ShouldBeEmpty();

        using var stream = new MemoryStream();
        var emitted = outcome.Compilation.Emit(stream);
        emitted.Success.ShouldBeTrue(string.Join("\n", emitted.Diagnostics));

        return SpecificationBindings.In(Assembly.Load(stream.ToArray()));
    }

    [Fact]
    public void a_test_binds_by_the_command_its_when_step_sends()
    {
        var all = bindings();

        var specified = all.Single(x => x.Identity == "ReviewVolunteerApplication/volunteer application reviewed");
        specified.CommandType!.FullName.ShouldBe("Shelter.VolunteeringAndHomeChecks.ReviewVolunteerApplication");
        specified.SliceName.ShouldBeNull();

        // new T(...) names the command as well as Specify<T>() does
        all.Single(x => x.Identity == "ReviewVolunteerApplication/reviewed from a new record")
            .CommandType!.Name.ShouldBe("ReviewVolunteerApplication");
    }

    [Fact]
    public void bobcat_slice_names_the_slice_and_a_methods_attribute_replaces_its_classes()
    {
        var all = bindings();

        var view = all.Single(x => x.Identity == "VolunteerApplicationsQueue/volunteer applications queue");
        view.SliceName.ShouldBe("VolunteerApplicationsQueue");
        view.Domain.ShouldBe("Volunteering");
        view.CommandType.ShouldBeNull();

        var rebound = all.Single(x => x.Identity == "VolunteerApplicationsQueue/rebound to another slice");
        rebound.SliceName.ShouldBe("ConfirmAppointment");
        rebound.Domain.ShouldBeNull();
        rebound.CommandType!.Name.ShouldBe("ConfirmAppointment");
    }

    [Fact]
    public void only_bobcat_feature_tests_are_listed()
    {
        var all = bindings();

        all.Count.ShouldBe(4);
        all.ShouldNotContain(x => x.Identity.Contains("ignored"));
        all.ShouldNotContain(x => x.Identity.Contains("not a test"));
    }
}
