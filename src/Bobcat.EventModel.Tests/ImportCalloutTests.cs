using System.Text.Json;
using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// bobcat#454: every guess the import makes is a callout with a stable code, its evidence and the
/// flag that settles it, so a tool such as Stoat can show them and let a user work through them.
/// </summary>
public class ImportCalloutTests
{
    private static EmlangImportResult import(string yaml, IReadOnlyCollection<string>? chapters = null, params AggregateOverride[] overrides)
        => EmlangImport.ToCurated(EmlangReader.Read(yaml), "Shelter", "Shelter", overrides, chapters);

    private static IReadOnlyList<ImportCallout> calloutsOf(EmlangImportResult result, string slice)
        => result.Callouts.Where(x => x.Slice == slice).ToList();

    [Fact]
    public void an_inferred_stream_start_names_its_evidence_and_the_flag_that_settles_it()
    {
        var callout = calloutsOf(import(AggregateInferenceTests.Shelter), "ApplyToVolunteer").Single();

        callout.Code.ShouldBe(ImportCalloutCodes.InferredStreamStart);
        callout.Aggregates.ShouldBe(["VolunteerApplication"]);
        callout.Events.ShouldBe(["VolunteerApplicationSubmitted"]);
        callout.Examples.ShouldBe(["submitted"]);
        callout.Override.ShouldBe("--starts-stream ApplyToVolunteer=VolunteerApplication");
    }

    [Fact]
    public void an_inferred_aggregate_names_the_events_its_examples_give_first()
    {
        var callout = calloutsOf(import(AggregateInferenceTests.Shelter), "ReviewVolunteerApplication").Single();

        callout.Code.ShouldBe(ImportCalloutCodes.InferredAgainst);
        callout.Aggregates.ShouldBe(["VolunteerApplication"]);
        callout.Events.ShouldBe(["VolunteerApplicationSubmitted"]);
        callout.Examples.ShouldBe(["reviewed"]);
        callout.Override.ShouldBe("--aggregate ReviewVolunteerApplication=VolunteerApplication");
    }

    [Fact]
    public void a_decision_drawing_on_several_streams_has_no_flag_to_settle_it()
    {
        var callouts = calloutsOf(import(AggregateInferenceTests.Shelter), "AcceptHomeCheckAssignment");

        var several = callouts.Single(x => x.Code == ImportCalloutCodes.SeveralStreams);
        several.Aggregates.ShouldBe(["HomeCheck", "VolunteerApplication"]);
        several.Override.ShouldBeNull();
        several.Message.ShouldContain("DCB decider");
    }

    [Fact]
    public void a_missing_aggregate_is_coded()
    {
        var result = import(
            """
            slices:
              Ping:
                steps:
                  - c: Ping
            """);

        var callout = calloutsOf(result, "Ping").Single();
        callout.Code.ShouldBe(ImportCalloutCodes.MissingAggregate);
        callout.Override.ShouldBe("--aggregate Ping=<Type>");
    }

    [Fact]
    public void every_callout_is_the_sentence_the_definition_writes()
    {
        var result = import(AggregateInferenceTests.Shelter);

        result.Callouts.ShouldNotBeEmpty();
        foreach (var slice in result.Model.Slices)
        {
            slice.Callouts.ShouldBe(calloutsOf(result, slice.Name).Select(x => x.Message).ToList());
        }

        result.Callouts.Select(x => x.Code).ShouldAllBe(x => new[]
        {
            ImportCalloutCodes.InferredStreamStart, ImportCalloutCodes.InferredAgainst, ImportCalloutCodes.SeveralStreams,
            ImportCalloutCodes.StreamStartFromUnimportedChapter, ImportCalloutCodes.StreamStartUnknown, ImportCalloutCodes.MissingAggregate
        }.Contains(x));
    }

    [Fact]
    public void an_aggregate_whose_only_evidence_nothing_imported_emits_is_asked_whether_it_starts_the_stream()
    {
        // CritterCrush's RequestHomeCheck: 'Application reviewed' is emitted by no slice in the import,
        // so nothing shows the HomeCheck stream existing before the command runs
        var result = import(AggregateInferenceTests.Shelter);
        var callout = calloutsOf(result, "RequestHomeCheck").Single();

        callout.Code.ShouldBe(ImportCalloutCodes.StreamStartFromUnimportedChapter);
        callout.Aggregates.ShouldBe(["HomeCheck"]);
        callout.Events.ShouldBe(["ApplicationReviewed"]);
        callout.Examples.ShouldBe(["requested"]);
        callout.Chapters.ShouldBeEmpty();
        callout.Message.ShouldStartWith("⚠ starts the HomeCheck stream?");
        callout.Message.ShouldContain("no imported slice");
        callout.Override.ShouldBe("--starts-stream RequestHomeCheck=HomeCheck");

        // The inference itself is unchanged: it is a question, not a correction
        result.Model.Slices.Single(x => x.Name == "RequestHomeCheck").Aggregates.ShouldBe(["HomeCheck"]);
        result.Report.ShouldContain(x => x.StartsWith("⚠ slice 'RequestHomeCheck' starts the HomeCheck stream?"));
    }

    private const string TwoChapters =
        """
        slices:
          Reviewing:
            steps:
              - c: Review application
              - e: Application reviewed
            tests:
              Reviewed:
                when:
                  - c: Review application
                then:
                  - e: Application reviewed
          HomeChecks:
            steps:
              - c: Request home check
              - e: Home check requested
              - c: Accept home check
              - e: Home check accepted
            tests:
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
                when:
                  - c: Accept home check
                then:
                  - e: Home check accepted
        """;

    [Fact]
    public void the_chapter_left_out_of_the_import_is_named()
    {
        var result = import(TwoChapters, ["HomeChecks"]);

        result.Model.Slices.Select(x => x.Name).ShouldBe(["RequestHomeCheck", "AcceptHomeCheck"]);
        result.Board!.Chapters.Select(x => x.Name).ShouldBe(["HomeChecks"]);

        var callout = calloutsOf(result, "RequestHomeCheck").Single();
        callout.Code.ShouldBe(ImportCalloutCodes.StreamStartFromUnimportedChapter);
        callout.Chapters.ShouldBe(["Reviewing"]);
        callout.Message.ShouldContain("come from chapter 'Reviewing', which was not imported");
    }

    [Fact]
    public void importing_the_chapter_that_emits_the_evidence_answers_the_question()
    {
        var result = import(TwoChapters);

        calloutsOf(result, "RequestHomeCheck").ShouldNotContain(x => x.Code == ImportCalloutCodes.StreamStartFromUnimportedChapter);
    }

    [Fact]
    public void starts_stream_says_it_outright_and_silences_the_question()
    {
        var result = import(AggregateInferenceTests.Shelter, null, AggregateOverride.ParseStartsStream("RequestHomeCheck=HomeCheck"));
        var slice = result.Model.Slices.Single(x => x.Name == "RequestHomeCheck");

        slice.StartsStream.ShouldBe("HomeCheck");
        slice.Aggregates.ShouldBeEmpty();
        calloutsOf(result, "RequestHomeCheck").ShouldBeEmpty();
    }

    [Fact]
    public void a_started_stream_never_takes_the_events_its_examples_give_first()
    {
        // Without --starts-stream the example joins 'Application reviewed' to the HomeCheck stream, and a
        // spec arranging it there contradicts a slice that starts HomeCheck
        var result = import(AggregateInferenceTests.Shelter, null, AggregateOverride.ParseStartsStream("RequestHomeCheck=HomeCheck"));

        result.Model.EventStreams["HomeCheckRequested"].ShouldBe("HomeCheck");
        result.Model.EventStreams["ApplicationReviewed"].ShouldNotBe("HomeCheck");
    }

    [Fact]
    public void starts_stream_alongside_aggregate_starts_one_and_decides_against_the_other()
    {
        var result = import(AggregateInferenceTests.Shelter, null,
            AggregateOverride.ParseStartsStream("AcceptHomeCheckAssignment=HomeCheck"),
            AggregateOverride.Parse("AcceptHomeCheckAssignment=VolunteerApplication"));
        var slice = result.Model.Slices.Single(x => x.Name == "AcceptHomeCheckAssignment");

        slice.StartsStream.ShouldBe("HomeCheck");
        slice.Aggregates.ShouldBe(["VolunteerApplication"]);
    }

    [Fact]
    public void an_unknown_chapter_is_refused_with_the_ones_the_board_has()
    {
        var failure = Should.Throw<ArgumentException>(() => import(TwoChapters, ["Nope"]));

        failure.Message.ShouldContain("No chapter named 'Nope'. The board has: Reviewing, HomeChecks");
    }

    [Fact]
    public void the_json_carries_every_callout_with_its_code_evidence_and_override()
    {
        var result = import(TwoChapters, ["HomeChecks"]);
        var json = ImportJson.Write("Shelter", result.Callouts, result.Report, [new ImportedFile("/app/Features/HomeChecks/RequestHomeCheck.cs", "write")]);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("model").GetString().ShouldBe("Shelter");

        var callout = root.GetProperty("callouts").EnumerateArray().Single(x => x.GetProperty("slice").GetString() == "RequestHomeCheck");
        callout.GetProperty("code").GetString().ShouldBe("stream-start-from-unimported-chapter");
        callout.GetProperty("aggregates").EnumerateArray().Select(x => x.GetString()).ShouldBe(["HomeCheck"]);
        callout.GetProperty("evidence").GetProperty("events").EnumerateArray().Select(x => x.GetString()).ShouldBe(["ApplicationReviewed"]);
        callout.GetProperty("evidence").GetProperty("examples").EnumerateArray().Select(x => x.GetString()).ShouldBe(["requested"]);
        callout.GetProperty("evidence").GetProperty("chapters").EnumerateArray().Select(x => x.GetString()).ShouldBe(["Reviewing"]);
        callout.GetProperty("override").GetString().ShouldBe("--starts-stream RequestHomeCheck=HomeCheck");

        root.GetProperty("report").GetArrayLength().ShouldBe(result.Report.Count);
        var file = root.GetProperty("files").EnumerateArray().Single();
        file.GetProperty("mode").GetString().ShouldBe("write");
    }

    [Fact]
    public void the_codes_are_stable()
    {
        // Tooling keys on these strings; they are only ever added to, never renamed
        ImportCalloutCodes.InferredStreamStart.ShouldBe("inferred-stream-start");
        ImportCalloutCodes.InferredAgainst.ShouldBe("inferred-against");
        ImportCalloutCodes.SeveralStreams.ShouldBe("several-streams");
        ImportCalloutCodes.StreamStartFromUnimportedChapter.ShouldBe("stream-start-from-unimported-chapter");
        ImportCalloutCodes.StreamStartUnknown.ShouldBe("stream-start-unknown");
        ImportCalloutCodes.MissingAggregate.ShouldBe("missing-aggregate");
    }
}
