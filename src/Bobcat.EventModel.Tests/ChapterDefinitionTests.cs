using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>bobcat#448: one EventModelDefinition per chapter, saying the chapter and its default aggregate once.</summary>
public class ChapterDefinitionTests
{
    private static CSharpModelWriter.Output generate()
    {
        var board = EmlangReader.Read(AggregateInferenceTests.Shelter);
        var model = EmlangImport.ToCurated(board, "Shelter", "Shelter").Model;
        return CSharpModelWriter.Write(model, "Shelter", null, perChapter: true);
    }

    [Fact]
    public void each_chapter_gets_a_definition_beside_its_stubs_with_no_name_and_no_repeated_chapter()
    {
        var generated = generate();

        generated.Definition.ShouldBeEmpty();
        generated.DefinitionFiles.Select(x => x.Path).OrderBy(x => x)
            .ShouldBe(["Features/Booking/BookingModel.cs", "Features/Volunteering/VolunteeringModel.cs"]);

        var volunteering = generated.DefinitionFiles.Single(x => x.Path.EndsWith("VolunteeringModel.cs")).Content;
        volunteering.ShouldContain("namespace Shelter.Volunteering;");
        volunteering.ShouldContain("public class VolunteeringModel : EventModelDefinition");
        volunteering.ShouldContain("model.InChapter(\"Volunteering\");");
        volunteering.ShouldNotContain(".InChapter(\"Volunteering\")\n");
        volunteering.ShouldNotContain("override string");
    }

    [Fact]
    public void the_aggregate_most_commands_decide_against_is_the_default_and_the_rest_stay_explicit()
    {
        var volunteering = generate().DefinitionFiles.Single(x => x.Path.EndsWith("VolunteeringModel.cs")).Content;

        volunteering.ShouldContain("model.ForAggregate<VolunteerApplication>();");
        volunteering.ShouldContain("// ⚠ inferred: 3 of the 5 commands here decide against VolunteerApplication");
        // Only AcceptHomeCheckAssignment, which decides against two streams, still says it
        System.Text.RegularExpressions.Regex.Matches(volunteering, @"\.Against<VolunteerApplication>\(\)").Count.ShouldBe(1);

        // A command against another aggregate, or several, keeps them; ForAggregate never re-declares
        volunteering.ShouldContain(".Against<HomeCheck>()");
        volunteering.ShouldNotContain("model.Aggregate<VolunteerApplication>()");
    }

    [Fact]
    public void the_per_chapter_definitions_compile_and_join_one_model_with_the_same_aggregates()
    {
        var generated = generate();
        var built = CSharpModelWriterTests.buildAll(generated);

        built.Count.ShouldBe(2);
        built.ShouldAllBe(x => x.Definition.Name == null);

        var slices = built.SelectMany(x => x.Model.Slices).ToDictionary(x => x.Name);
        slices["ReviewVolunteerApplication"].AggregateTypes.Select(x => x.Name).ShouldBe(["VolunteerApplication"]);
        slices["ReviewVolunteerApplication"].AggregateDeclaration.ShouldBe(AggregateDeclaration.Default);
        slices["ReviewVolunteerApplication"].Chapter.ShouldBe("Volunteering");

        slices["RequestHomeCheck"].AggregateTypes.Select(x => x.Name).ShouldBe(["HomeCheck"]);
        slices["RequestHomeCheck"].AggregateDeclaration.ShouldBe(AggregateDeclaration.Explicit);

        slices["AcceptHomeCheckAssignment"].AggregateTypes.Select(x => x.Name).OrderBy(x => x)
            .ShouldBe(["HomeCheck", "VolunteerApplication"]);

        slices["ConfirmAppointment"].AggregateTypes.Select(x => x.Name).ShouldBe(["Appointment"]);
        slices["ConfirmAppointment"].Chapter.ShouldBe("Booking");
    }

    [Fact]
    public void a_single_definition_is_still_the_default_for_the_library()
    {
        var board = EmlangReader.Read(AggregateInferenceTests.Shelter);
        var model = EmlangImport.ToCurated(board, "Shelter", "Shelter").Model;
        var generated = CSharpModelWriter.Write(model, "Shelter");

        generated.DefinitionFiles.ShouldBeEmpty();
        generated.Definition.ShouldContain(".InChapter(\"Volunteering\")");
    }
}
