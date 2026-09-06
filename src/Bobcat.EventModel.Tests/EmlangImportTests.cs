using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using Shouldly;

namespace Bobcat.EventModel.Tests;

public class EmlangImportTests
{
    private static CuratedModelFile import(string yaml, out IReadOnlyList<string> report)
    {
        var result = EmlangImport.ToCurated(EmlangReader.Read(yaml), "K9Crush");
        report = result.Report;
        return result.Model;
    }

    private static CuratedModelFile import(string yaml) => import(yaml, out _);

    private const string SwipeChapter =
        """
        slices:
          TheSwiper:
            steps:
              - t: Member/Discovery Feed
              - c: Member/Swipe On Dog
              - e: Member/Dog Liked
              - e: Member/Dog Passed
              - c: System/Detect Mutual Match
                props: { triggeredBy: Dog Liked, module: Discovery }
              - e: System/Mutual Match Detected
              - v: Member/Match List
                props: { dogId: dog_204, name: Luna }
            tests:
              ALikeIsRecorded:
                given: [{ e: Member/Dog Liked }]
                when: [{ c: Member/Swipe On Dog }]
                then: [{ e: Member/Dog Liked }]
              MatchesShow:
                then: [{ v: Member/Match List }]
        """;

    [Fact]
    public void a_screen_command_event_run_is_a_command_slice_triggered_by_the_screen()
    {
        var slice = import(SwipeChapter).Slices.First(x => x.Name == "SwipeOnDog");

        slice.Pattern.ShouldBe("Command");
        slice.Command.ShouldBe("SwipeOnDog");
        slice.Events.ShouldBe(["DogLiked", "DogPassed"]);
        slice.Trigger!.Kind.ShouldBe("Human");
        slice.Trigger.Label.ShouldBe("Discovery Feed");
    }

    [Fact]
    public void triggered_by_makes_an_automation_slice_and_the_board_closes_the_pattern_gap()
    {
        // Gherkin cannot express an automation's trigger, so the derived Pattern stays null
        // there — the board is the source that CAN say it, which is #202's whole point.
        var slice = import(SwipeChapter).Slices.First(x => x.Name == "DetectMutualMatch");

        slice.Pattern.ShouldBe("Automation");
        slice.Trigger!.Kind.ShouldBe("MessageHandler");
        slice.Trigger.Label.ShouldBe("Dog Liked");
        slice.Domain.ShouldBe("Discovery");
        slice.Events.ShouldBe(["MutualMatchDetected"]);
    }

    [Fact]
    public void a_view_becomes_a_view_slice_keeping_sample_props_as_hints_not_roles()
    {
        var slice = import(SwipeChapter).Slices.First(x => x.Name == "MatchList");

        slice.Pattern.ShouldBe("View");
        slice.ReadModels.ShouldBe(["MatchList"]);
        slice.Events.ShouldBeEmpty();
        slice.Elements["MatchList"].Fields["name"].ShouldBe("Luna");
    }

    [Fact]
    public void tests_attach_to_the_slice_their_when_command_names()
    {
        var slice = import(SwipeChapter).Slices.First(x => x.Name == "SwipeOnDog");

        var scenario = slice.Specifications!.Scenarios.Single();
        scenario.Name.ShouldBe("ALikeIsRecorded");
        scenario.Given.Single().Event.ShouldBe("DogLiked");
        scenario.When!.Command.ShouldBe("SwipeOnDog");
        scenario.Then.Single().Event.ShouldBe("DogLiked");
    }

    [Fact]
    public void an_assertion_only_test_attaches_to_the_view_slice()
    {
        var slice = import(SwipeChapter).Slices.First(x => x.Name == "MatchList");

        var scenario = slice.Specifications!.Scenarios.Single();
        scenario.When.ShouldBeNull();
        scenario.Then.Single().ReadModel.ShouldBe("MatchList");
    }

    [Fact]
    public void the_same_command_in_two_chapters_folds_into_one_slice()
    {
        // A chapter is a persona timeline, not a slice — slice name is the merge key everywhere.
        var model = import(
            """
            slices:
              ChapterOne:
                steps:
                  - c: Member/Swipe On Dog
                  - e: Member/Dog Liked
              ChapterTwo:
                steps:
                  - c: Member/Swipe On Dog
                  - e: Member/Dog Passed
            """, out var report);

        var slice = model.Slices.Single(x => x.Name == "SwipeOnDog");
        slice.Events.ShouldBe(["DogLiked", "DogPassed"]);
        report.ShouldContain(x => x.Contains("folded into existing slice 'SwipeOnDog'"));
    }

    [Fact]
    public void an_exception_step_lands_as_a_hotspot_on_the_open_slice()
    {
        var model = import(
            """
            slices:
              Chapter:
                steps:
                  - c: Member/Swipe On Dog
                  - x: Member/Swipe Blocked Profile Removed
            """);

        model.Slices.Single().Hotspots.Single().ShouldBe("Swipe Blocked Profile Removed");
    }

    [Fact]
    public void guesses_and_orphans_are_reported_never_silent()
    {
        import(
            """
            slices:
              Chapter:
                steps:
                  - e: Member/Orphan Event
                tests:
                  Unmatched:
                    when: [{ c: Member/Never Declared }]
                    then: [{ e: Member/Whatever }]
            """, out var report);

        report.ShouldContain(x => x.Contains("precedes any command"));
        report.ShouldContain(x => x.Contains("names no known slice"));
    }

    [Fact]
    public void the_same_test_on_a_folded_slice_keeps_the_first_and_says_so()
    {
        // Identities must stay unique to join run evidence, so a duplicate is a report line,
        // never a second scenario — the full K9CRUSH corpus hits this on folded slices.
        var model = import(
            """
            slices:
              ChapterOne:
                steps: [{ c: Member/Confirm Profile }, { e: Member/Profile Confirmed }]
                tests:
                  ProfileConfirmed:
                    when: [{ c: Member/Confirm Profile }]
                    then: [{ e: Member/Profile Confirmed }]
              ChapterTwo:
                steps: [{ c: Member/Confirm Profile }]
                tests:
                  ProfileConfirmed:
                    when: [{ c: Member/Confirm Profile }]
                    then: [{ e: Member/Profile Confirmed }]
            """, out var report);

        model.Slices.Single(x => x.Name == "ConfirmProfile").Specifications!.Scenarios.Count.ShouldBe(1);
        report.ShouldContain(x => x.Contains("kept the first"));
    }

    [Fact]
    public void the_boards_naming_rule_is_pascal_runs_of_alphanumerics()
    {
        EmlangImport.PascalName("RSVP Blocked: Event Full").ShouldBe("RSVPBlockedEventFull");
        EmlangImport.PascalName("The Would-Be Adopter").ShouldBe("TheWouldBeAdopter");
        EmlangImport.PascalName("swipe on dog").ShouldBe("SwipeOnDog");
    }

    [Fact]
    public void the_import_round_trips_through_the_curated_reader_and_maps_clean()
    {
        var yaml = CuratedModelWriter.Write(import(SwipeChapter));
        var reading = CuratedModelReader.Read(yaml);

        reading.Problems.ShouldBeEmpty();

        var descriptor = CuratedModelMapper.ToDescriptor(reading.File!);
        descriptor.Name.ShouldBe("K9Crush");
        descriptor.Slices.Count.ShouldBe(3);
        descriptor.Slices.SelectMany(x => x.Specifications)
            .ShouldContain(x => x.Identity == "SwipeOnDog/ALikeIsRecorded");
    }

    [Fact]
    public void the_sniffer_tells_the_two_formats_apart()
    {
        EventModelFileSniffer.Sniff(SwipeChapter).ShouldBe(EventModelFileKind.Emlang);
        EventModelFileSniffer.Sniff("schema: 1\nmodel: X\nslices: []").ShouldBe(EventModelFileKind.Curated);
        EventModelFileSniffer.Sniff("something: else").ShouldBe(EventModelFileKind.Unknown);
    }
}
