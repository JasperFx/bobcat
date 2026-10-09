using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using Shouldly;

namespace Bobcat.EventModel.Tests;

public class EmlangImportTests
{
    private static ImportedEventModel import(string yaml, out IReadOnlyList<string> report)
    {
        var result = EmlangImport.ToCurated(EmlangReader.Read(yaml), "K9Crush");
        report = result.Report;
        return result.Model;
    }

    private static ImportedEventModel import(string yaml) => import(yaml, out _);

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
        scenario.Name.ShouldBe("a like is recorded"); // the title its projected spec reports (bobcat#435)
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

        // Issue #298: one chapter per slice, so the fold keeps the first and the report says so.
        slice.Chapter.ShouldBe("ChapterOne");
        report.ShouldContain(x => x.Contains("Kept chapter 'ChapterOne'"));
    }

    [Fact]
    public void every_slice_carries_the_chapter_it_was_segmented_from()
    {
        // Issue #298: EmlangImport used to keep the chapter only as prose in Notes. Every tool in
        // the space uses chapters as the answer to "zoom into a part", so the name now survives as
        // the slice's Chapter, and the curated file writes it.
        var model = import(SwipeChapter, out _);

        model.Slices.Select(x => x.Chapter).Distinct().ShouldBe(["TheSwiper"]);
    }

    [Fact]
    public void two_chapters_give_their_own_slices_their_own_chapter()
    {
        var model = import(
            """
            slices:
              Onboarding:
                steps:
                  - c: Member/Enroll
                  - e: Member/Enrolled
              Swiping:
                steps:
                  - c: Member/Swipe On Dog
                  - e: Member/Dog Liked
                  - v: Member/Match List
            """);

        model.Slices.Single(x => x.Name == "Enroll").Chapter.ShouldBe("Onboarding");
        model.Slices.Single(x => x.Name == "SwipeOnDog").Chapter.ShouldBe("Swiping");
        model.Slices.Single(x => x.Name == "MatchList").Chapter.ShouldBe("Swiping");
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
    public void a_view_consumes_the_events_since_the_chapter_start_or_the_last_view()
    {
        // Issue #297: the `e:` steps before a `v:` are its inputs. The segmentation already used
        // that run to decide a `v:` opens a View slice; now it is recorded instead of discarded.
        const string chapter =
            """
            slices:
              Feeds:
                steps:
                  - c: Member/Do A
                  - e: Member/A Happened
                  - e: Member/B Happened
                  - v: Member/V List
                  - c: Member/Do C
                  - e: Member/C Happened
                  - v: Member/W List
            """;

        var model = import(chapter, out var report);

        model.Slices.Single(x => x.Name == "VList").ConsumedEvents.ShouldBe(["AHappened", "BHappened"]);
        model.Slices.Single(x => x.Name == "WList").ConsumedEvents.ShouldBe(["CHappened"]);
        report.ShouldContain("chapter 'Feeds': View slice 'VList' consumes 2 event(s): AHappened, BHappened.");
        report.ShouldContain("chapter 'Feeds': View slice 'WList' consumes 1 event(s): CHappened.");
    }

    [Fact]
    public void the_swipe_chapters_match_list_consumes_every_event_that_precedes_it()
    {
        var slice = import(SwipeChapter).Slices.Single(x => x.Name == "MatchList");

        slice.ConsumedEvents.ShouldBe(["DogLiked", "DogPassed", "MutualMatchDetected"]);
        // Consumed is not emitted: the view's own `events:` stays empty.
        slice.Events.ShouldBeEmpty();
    }

    [Fact]
    public void a_view_with_no_preceding_event_is_reported_rather_than_left_silently_empty()
    {
        const string chapter =
            """
            slices:
              Bare:
                steps:
                  - v: Member/Lonely List
            """;

        import(chapter, out var report);

        report.ShouldContain(x => x.Contains("View slice 'LonelyList' consumes no event"));
    }

    [Fact]
    public void a_view_folded_from_a_second_chapter_unions_its_consumed_events()
    {
        const string board =
            """
            slices:
              One:
                steps:
                  - c: Member/Do A
                  - e: Member/A Happened
                  - v: Member/Shared List
              Two:
                steps:
                  - c: Member/Do B
                  - e: Member/B Happened
                  - v: Member/Shared List
            """;

        var slice = import(board).Slices.Single(x => x.Name == "SharedList");
        slice.ConsumedEvents.ShouldBe(["AHappened", "BHappened"]);
    }

    [Fact]
    public void the_boards_naming_rule_is_pascal_runs_of_alphanumerics()
    {
        EmlangImport.PascalName("RSVP Blocked: Event Full").ShouldBe("RSVPBlockedEventFull");
        EmlangImport.PascalName("The Would-Be Adopter").ShouldBe("TheWouldBeAdopter");
        EmlangImport.PascalName("swipe on dog").ShouldBe("SwipeOnDog");
    }

    [Fact]
    public void the_import_maps_clean_to_a_descriptor()
    {
        // Went through a YAML write-then-read until issue #406 retired the writer. The hop was
        // never the claim — "what the importer produced maps to a clean descriptor" is, and the
        // C# path that replaced the YAML one is pinned by CSharpModelWriterTests, which compiles
        // and runs its output.
        var descriptor = ImportedModelMapper.ToDescriptor(import(SwipeChapter));
        descriptor.Name.ShouldBe("K9Crush");
        descriptor.Slices.Count.ShouldBe(3);
        descriptor.Slices.SelectMany(x => x.Specifications)
            .ShouldContain(x => x.Identity == "SwipeOnDog/a like is recorded");
    }

    [Fact]
    public void the_sniffer_tells_the_two_formats_apart()
    {
        EventModelFileSniffer.Sniff(SwipeChapter).ShouldBe(EventModelFileKind.Emlang);
        EventModelFileSniffer.Sniff("schema: 1\nmodel: X\nslices: []").ShouldBe(EventModelFileKind.Curated);
        EventModelFileSniffer.Sniff("something: else").ShouldBe(EventModelFileKind.Unknown);
    }
}

/// <summary>
/// Issue #422: emlang as published models actually write it. Each fixture is a small hand-written
/// stand-in for a shape found in a public model, not a copy of one.
/// </summary>
public class EmlangInTheWildTests
{
    private static ImportedEventModel import(string yaml, out IReadOnlyList<string> report)
    {
        var result = EmlangImport.ToCurated(EmlangReader.Read(yaml), "Kitchen");
        report = result.Report;
        return result.Model;
    }

    private static ImportedEventModel import(string yaml) => import(yaml, out _);

    private const string TwoDocuments =
        """
        # A preamble of comments only, before the first document marker
        ---
        slices:
          Place order:
            steps:
              - t: Customer / Checkout
              - c: Place order
                props:
                  order id: uuid
                  total: decimal
              - e: Order / Order placed
            tests:
              Placing an order:
                given: # nothing yet
                when:
                  - c: Place order
                    props:
                      order id: order-1
                      total: 21.50
                then:
                  - e: Order / Order placed
                    props:
                      order id: order-1
        ---
        slices:
          Bake pizza:
            - event: Order / Order placed
            - command: Kitchen / Bake pizza
            - event: Kitchen / Pizza baked
        """;

    [Fact]
    public void every_document_in_the_file_is_read()
    {
        var board = EmlangReader.Read(TwoDocuments);

        board.Chapters.Select(x => x.Name).ShouldBe(["Place order", "Bake pizza"]);
    }

    [Fact]
    public void a_comment_only_preamble_document_does_not_hide_the_file_from_the_sniffer()
    {
        EventModelFileSniffer.Sniff(TwoDocuments).ShouldBe(EventModelFileKind.Emlang);
    }

    [Fact]
    public void a_slice_written_as_just_its_steps_is_read_with_the_long_keys()
    {
        var chapter = EmlangReader.Read(TwoDocuments).Chapters.Single(x => x.Name == "Bake pizza");

        chapter.Steps.Select(x => (x.Kind, x.Actor, x.Label)).ShouldBe([
            (EmlangElementKind.Event, "Order", "Order placed"),
            (EmlangElementKind.Command, "Kitchen", "Bake pizza"),
            (EmlangElementKind.Event, "Kitchen", "Pizza baked")
        ]);
        chapter.Tests.ShouldBeEmpty();
    }

    [Fact]
    public void every_long_key_reads_as_its_short_one()
    {
        var chapter = EmlangReader.Read(
            """
            slices:
              Everything:
                - trigger: Screen
                - command: Do it
                - event: Done
                - view: Summary
                - exception: Refused
            """).Chapters.Single();

        chapter.Steps.Select(x => x.Kind).ShouldBe([
            EmlangElementKind.Screen, EmlangElementKind.Command, EmlangElementKind.Event,
            EmlangElementKind.View, EmlangElementKind.Error
        ]);
    }

    [Fact]
    public void a_reference_reads_with_or_without_spaces_around_the_slash()
    {
        var steps = EmlangReader.Read(
            """
            slices:
              Refs:
                - e: Order / Item added
                - e: Order/Item removed
            """).Chapters.Single().Steps;

        steps.Select(x => (x.Actor, x.Label)).ShouldBe([("Order", "Item added"), ("Order", "Item removed")]);
    }

    [Fact]
    public void a_test_keeps_its_example_values_and_an_empty_given_is_no_given()
    {
        var test = EmlangReader.Read(TwoDocuments).Chapters[0].Tests.Single();

        test.Given.ShouldBeEmpty();
        test.When.Single().Props.ShouldBe(new Dictionary<string, string> { ["order id"] = "order-1", ["total"] = "21.50" });
        test.Then.Single().Props["order id"].ShouldBe("order-1");
    }

    [Fact]
    public void prop_values_that_are_lists_numbers_or_empty_are_read_rather_than_refused()
    {
        var then = EmlangReader.Read(
            """
            slices:
              Summary:
                steps:
                  - v: Order summary
                tests:
                  Two items:
                    then:
                      - v: Order summary
                        props:
                          items:
                            - margherita/9.99
                            - pepperoni/11.99
                          total: 21.98
                          note:
            """).Chapters.Single().Tests.Single().Then.Single();

        then.Props["items"].ShouldBe("margherita/9.99, pepperoni/11.99");
        then.Values["items"].ShouldBe(new List<object> { "margherita/9.99", "pepperoni/11.99" });
        then.Props["total"].ShouldBe("21.98");
        then.Props["note"].ShouldBe("");
        then.Values["note"].ShouldBeNull();
    }

    [Fact]
    public void test_values_reach_the_curated_scenario()
    {
        var scenario = import(TwoDocuments).Slices.Single(x => x.Name == "PlaceOrder")
            .Specifications!.Scenarios.Single();

        scenario.Name.ShouldBe("placing an order");
        scenario.Given.ShouldBeEmpty();
        scenario.When!.With["order id"].ShouldBe("order-1");
        scenario.Then.Single().Event.ShouldBe("OrderPlaced");
        scenario.Then.Single().With["order id"].ShouldBe("order-1");
    }

    [Fact]
    public void a_type_a_step_declares_wins_over_a_tests_sample_value()
    {
        var model = import(TwoDocuments);

        CSharpModelWriter.FieldsOf(model, "PlaceOrder").ShouldBe([
            new CSharpModelWriter.StubField("OrderId", "Guid"),
            new CSharpModelWriter.StubField("Total", "decimal")
        ]);
    }

    [Fact]
    public void an_element_with_no_declared_props_takes_its_fields_from_test_samples()
    {
        var model = import(TwoDocuments);

        // An undeclared identity is a Guid whatever its sample looks like (bobcat#423)
        CSharpModelWriter.FieldsOf(model, "OrderPlaced").ShouldBe([new CSharpModelWriter.StubField("OrderId", "Guid")]);
        CSharpModelWriter.FieldsOf(model, "PizzaBaked").ShouldBeEmpty();
    }

    [Fact]
    public void the_stub_file_writes_positional_records_for_elements_with_fields()
    {
        var stubs = CSharpModelWriter.Write(import(TwoDocuments), "Kitchen").AllStubs();

        // The model marks no identity on the command, so it also gets the conventional Id (bobcat#438)
        stubs.ShouldContain("public record PlaceOrder(Guid Id, Guid OrderId, decimal Total);");
        stubs.ShouldContain("public record PizzaBaked;");
        stubs.ShouldContain("using System;");
    }

    [Fact]
    public void emlang_type_spellings_are_known_types()
    {
        var model = import(
            """
            slices:
              Book:
                - c: Book session
                  props:
                    session id: uuid
                    starts: datetime
                    day: date
                    seats: integer
                    paid: boolean
            """);

        CSharpModelWriter.FieldsOf(model, "BookSession").Select(x => x.Type)
            .ShouldBe(["Guid", "DateTimeOffset", "DateOnly", "int", "bool"]);
    }

    [Fact]
    public void a_view_given_and_a_refusal_with_props_are_reported_not_silently_dropped()
    {
        import(
            """
            slices:
              Register:
                steps:
                  - c: Register
                  - e: Registered
                  - v: Directory
                tests:
                  Duplicate email:
                    given:
                      - v: Directory
                        props:
                          email: ann@example.com
                    when:
                      - c: Register
                    then:
                      - x: Email in use
                        props:
                          email: ann@example.com
            """, out var report);

        report.ShouldContain(x => x.Contains("gives the view 'Directory'"));
        report.ShouldContain(x => x.Contains("refuses with 'Email in use' carrying props (email)"));
    }

    [Fact]
    public void a_screen_in_then_is_not_mistaken_for_an_event()
    {
        var scenario = import(
            """
            slices:
              Send:
                steps:
                  - c: Send message
                  - e: Message sent
                tests:
                  Sent:
                    when:
                      - c: Send message
                    then:
                      - e: Message sent
                      - t: Inbox
            """).Slices.Single(x => x.Name == "SendMessage").Specifications!.Scenarios.Single();

        scenario.Then.Select(x => x.Event).ShouldBe(["MessageSent"]);
    }
}

public class EmlangProcessorSliceTests
{
    private static ImportedEventModel import(string yaml, out IReadOnlyList<string> report)
    {
        var result = EmlangImport.ToCurated(EmlangReader.Read(yaml), "Kitchen");
        report = result.Report;
        return result.Model;
    }

    [Fact]
    public void an_event_before_a_command_with_no_screen_triggers_it_as_an_automation()
    {
        var model = import(
            """
            slices:
              Send receipt:
                - e: Order / Payment confirmed
                - c: Send receipt
                - e: Receipt sent
            """, out var report);

        var slice = model.Slices.Single(x => x.Name == "SendReceipt");
        slice.Pattern.ShouldBe("Automation");
        slice.Trigger!.Kind.ShouldBe("MessageHandler");
        slice.Trigger.Label.ShouldBe("Payment confirmed");
        slice.Events.ShouldBe(["ReceiptSent"]);
        report.ShouldNotContain(x => x.Contains("precedes any command"));
    }

    [Fact]
    public void a_screen_still_makes_the_command_human_even_after_an_event()
    {
        var slice = import(
            """
            slices:
              Reorder:
                - e: Order delivered
                - t: Customer / Order history
                - c: Reorder
            """, out _).Slices.Single(x => x.Name == "Reorder");

        slice.Pattern.ShouldBe("Command");
        slice.Trigger!.Kind.ShouldBe("Human");
    }

    [Fact]
    public void events_only_are_still_reported_as_unattached()
    {
        import(
            """
            slices:
              Brainstorm:
                - e: Item added
                - e: Cart cleared
            """, out var report);

        report.Count(x => x.Contains("precedes any command")).ShouldBe(2);
    }

    [Fact]
    public void a_test_naming_no_command_or_view_attaches_to_the_chapters_only_slice()
    {
        var model = import(
            """
            slices:
              View summary:
                steps:
                  - e: Item added
                  - v: Order summary
                tests:
                  Nothing ordered yet:
                    given:
                    then:
            """, out _);

        model.Slices.Single(x => x.Name == "OrderSummary").Specifications!.Scenarios
            .Single().Name.ShouldBe("nothing ordered yet");
    }
}
