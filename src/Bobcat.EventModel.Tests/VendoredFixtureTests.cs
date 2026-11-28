using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bobcat.EventModel.Emlang;
using Json.Schema;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// bobcat#847: the readers run against what the Event Modeling tools actually publish, vendored under
/// <c>Fixtures/Import/vendored</c> with their provenance in the README there. The Emlang Project's
/// examples and schema prove the YAML reader against the community grammar; eventmodelers.ai's own
/// demo board and the eventsourcing-book board prove the EM Spec JSON reader against the platform's
/// export. Every file is untrusted data: read, never executed.
/// </summary>
public class VendoredFixtureTests
{
    private static string vendored([CallerFilePath] string path = "")
        => Path.Combine(Path.GetDirectoryName(path)!, "Fixtures", "Import", "vendored");

    private static string emlang(string file) => Path.Combine(vendored(), "emlang", file);
    private static string emspec(string file) => Path.Combine(vendored(), "emspec", file);

    // Built once: JsonSchema.Net registers a schema's $id globally and refuses to register it twice
    private static readonly Lazy<JsonSchema> emlangSchema = new(() => JsonSchema.FromFile(emlang("schema.json")));

    public static TheoryData<string> EmlangExamples => new(
        Directory.GetFiles(Path.Combine(vendored(), "emlang", "examples"), "*.yaml").Select(Path.GetFileName).OrderBy(x => x).Select(x => x!));

    // ---- Emlang Project ----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(EmlangExamples))]
    public void every_emlang_example_validates_against_the_projects_schema(string example)
    {
        var schema = emlangSchema.Value;
        var documents = EmlangReader.Documents(File.ReadAllText(emlang(Path.Combine("examples", example))));

        documents.ShouldNotBeEmpty();
        foreach (var document in documents)
        {
            var result = schema.Evaluate(JsonSerializer.SerializeToElement(toJson(document)), new EvaluationOptions { OutputFormat = OutputFormat.List });
            result.IsValid.ShouldBeTrue($"{example}: {string.Join("; ", result.Details.Where(x => x.Errors is { Count: > 0 }).Select(x => $"{x.InstanceLocation}: {string.Join(", ", x.Errors!.Values)}"))}");
        }
    }

    [Theory]
    [MemberData(nameof(EmlangExamples))]
    public void every_emlang_example_imports(string example)
    {
        var board = EmlangReader.Read(File.ReadAllText(emlang(Path.Combine("examples", example))));
        var result = EmlangImport.ToCurated(board, "Example");

        board.Chapters.ShouldNotBeEmpty();
        foreach (var chapter in board.Chapters)
        {
            // An emlang slice states no chapter and no slice type: both stay the import's to settle
            chapter.BoardChapter.ShouldBeNull();
            chapter.SliceType.ShouldBeNull();
        }

        // brainstorming.yaml is an event storm with no command or view, so it segments into nothing
        if (board.Chapters.Any(c => c.Steps.Any(s => s.Kind is EmlangElementKind.Command or EmlangElementKind.View)))
        {
            result.Model.Slices.ShouldNotBeEmpty();
        }
    }

    [Theory]
    [MemberData(nameof(EmlangExamples))]
    public void every_tests_block_lowers_to_a_scenario(string example)
    {
        var board = EmlangReader.Read(File.ReadAllText(emlang(Path.Combine("examples", example))));
        var result = EmlangImport.ToCurated(board, "Example");

        var tests = board.Chapters.SelectMany(c => c.Tests.Select(t => (Chapter: c.Name, t.Name))).ToList();
        var scenarios = result.Model.Slices.SelectMany(s => s.Specifications?.Scenarios ?? []).ToList();

        scenarios.Count.ShouldBe(tests.Count,
            $"{example}: {string.Join("; ", result.Report.Where(x => x.Contains("test '") && x.StartsWith('⚠')))}");
        foreach (var scenario in scenarios)
        {
            scenario.Source.ShouldNotBeNull();
            tests.ShouldContain((scenario.SourceChapter!.Name, scenario.Source!.Name));
        }
    }

    [Theory]
    [MemberData(nameof(EmlangExamples))]
    public void every_emlang_test_lowers_to_a_scenario_model_that_says_what_the_test_says(string example)
    {
        var board = EmlangReader.Read(File.ReadAllText(emlang(Path.Combine("examples", example))));

        foreach (var test in board.Chapters.SelectMany(c => c.Tests))
        {
            var model = test.Model;
            model.Name.ShouldBe(test.Name);
            model.GivenEvents.Count.ShouldBe(test.Given.Count(x => x.Kind == EmlangElementKind.Event));
            model.Context.Count.ShouldBe(test.Given.Count(x => x.Kind == EmlangElementKind.View));
            (model.Act is Scenarios.MessageReceived).ShouldBe(test.When.Any(x => x.Kind == EmlangElementKind.Command));
            model.Outcomes.Count.ShouldBe(test.Then.Count);
            model.Refuses.ShouldBe(test.Then.Any(x => x.Kind == EmlangElementKind.Error));
        }
    }

    [Fact]
    public void the_pizza_boards_tests_keep_their_given_when_then_and_values()
    {
        var board = EmlangReader.Read(File.ReadAllText(emlang(Path.Combine("examples", "pizza.yaml"))));
        var model = EmlangImport.ToCurated(board, "Pizza").Model;

        var addItem = model.Slices.Single(x => x.Name == "AddItemToOrder");
        addItem.Pattern.ShouldBe("Command");
        addItem.Trigger!.Label.ShouldBe("Menu");

        var scenario = addItem.Specifications!.Scenarios.Single(x => x.Source!.Name == "Add item to existing order");
        scenario.Given.Select(x => x.Event).ShouldBe(["OrderStarted", "ItemAdded"]);
        scenario.When!.Command.ShouldBe("AddItemToOrder");
        scenario.When.With["item id"].ShouldBe("cheese-pizza");
        scenario.Then.Select(x => x.Event ?? x.ReadModel).ShouldBe(["ItemAdded", "OrderSummary"]);
        scenario.Then[1].Contains["total"].ShouldBe("33.98");
    }

    [Fact]
    public void the_specs_acronym_prefixes_read_as_their_short_forms()
    {
        // Emlang spec v1.0.0, "Elements": t/trg/trigger, c/cmd/command, e/evt/event, x/err/exception are equivalent
        var board = EmlangReader.Read(
            """
            slices:
              Registration:
                steps:
                  - trg: Customer/Registration Form
                  - cmd: Register User
                  - evt: User/User Registered
                  - err: Email Already In Use
                  - view: User Profile
            """);

        board.Chapters.Single().Steps.Select(x => x.Kind).ShouldBe([
            EmlangElementKind.Screen, EmlangElementKind.Command, EmlangElementKind.Event, EmlangElementKind.Error, EmlangElementKind.View]);
    }

    // ---- EM Spec JSON: eventmodelers.ai's own export -----------------------------------------------

    [Fact]
    public void the_nebulit_demo_board_reads_every_slice_with_its_chapter_and_slice_type()
    {
        var board = EventModelersJsonReader.Read(File.ReadAllText(emspec(Path.Combine("nebulit-demo", "config.json"))));

        board.Chapters.Count.ShouldBe(16);
        board.Chapters.ShouldAllBe(x => x.BoardChapter == "Understanding Eventsourcing");
        board.Chapters.Select(x => (x.Name, x.SliceType)).ShouldContain(("Add Item", "Command"));
        board.Chapters.Select(x => (x.Name, x.SliceType)).ShouldContain(("cart items", "View"));
        board.Chapters.Select(x => (x.Name, x.SliceType)).ShouldContain(("Change Inventory", "Automation"));
        board.Chapters.Select(x => (x.Name, x.SliceType)).ShouldContain(("Item Added", (string?)null));
        board.Chapters.Count(x => x.SliceType == "Command").ShouldBe(4);
        board.Chapters.Count(x => x.SliceType == "View").ShouldBe(5);
        board.Chapters.Count(x => x.SliceType == null).ShouldBe(3);
        board.Chapters.Count(x => x.SliceType == "Automation").ShouldBe(4);

        // Every element on the demo board carries the platform's "default" aggregate: none is a stream
        board.Chapters.SelectMany(x => x.Steps).ShouldAllBe(x => x.Stream == null);

        var addItem = board.Chapters.Single(x => x.Name == "Add Item").Steps.Single(x => x.Kind == EmlangElementKind.Command);
        addItem.Props.Keys.ShouldContain("aggregateId");
        addItem.Props["aggregateId"].ShouldBe("uuid");
    }

    [Fact]
    public void the_nebulit_demo_board_imports_into_one_chapter_with_the_boards_patterns()
    {
        var board = EventModelersJsonReader.Read(File.ReadAllText(emspec(Path.Combine("nebulit-demo", "config.json"))));
        var result = EmlangImport.ToCurated(board, "UnderstandingEventsourcing");

        result.Model.Slices.ShouldNotBeEmpty();
        result.Model.Slices.ShouldAllBe(x => x.Chapter == "Understanding Eventsourcing");
        result.Model.Slices.Single(x => x.Name == "AddItem").Pattern.ShouldBe("Command");
        result.Model.Slices.Single(x => x.Name == "ChangeInventory").Pattern.ShouldBe("Automation");
        result.Model.Slices.Single(x => x.Name == "ChangeInventory").Trigger!.Kind.ShouldBe("MessageHandler");
        result.Model.Slices.Single(x => x.Name == "CartItems").Pattern.ShouldBe("View");
        result.Model.EventStreams.Values.ShouldNotContain("Default");

        // --chapter by the board's chapter takes the whole board
        EmlangImport.SelectChapters(board, ["Understanding Eventsourcing"]).Chapters.Count.ShouldBe(16);
    }

    [Fact]
    public void a_nebulit_slice_json_reads_as_one_slice_in_its_chapter()
    {
        var board = EventModelersJsonReader.Read(File.ReadAllText(emspec(Path.Combine("nebulit-demo", "submitcart.slice.json"))));

        var slice = board.Chapters.Single();
        slice.Name.ShouldBe("Submit Cart");
        slice.BoardChapter.ShouldBe("Understanding Eventsourcing");
        slice.SliceType.ShouldBe("Command");
        slice.Steps.Select(x => (x.Kind, x.Label)).ShouldBe([
            (EmlangElementKind.Screen, "title"),
            (EmlangElementKind.Command, "Submit Cart"),
            (EmlangElementKind.Event, "Cart Submitted"),
        ]);
        slice.Steps[2].Props["orderedProducts"].ShouldBe("List<string>");
        slice.Steps[2].Props["totalPrice"].ShouldBe("double");
    }

    [Fact]
    public void the_eventsourcing_book_board_reads_its_declared_aggregates_and_every_field()
    {
        var board = EventModelersJsonReader.Read(File.ReadAllText(emspec(Path.Combine("eventsourcing-book", "config.json"))));

        board.Chapters.Count.ShouldBe(13);
        // This board states no chapter, so each slice is its own, as it was before bobcat#845
        board.Chapters.ShouldAllBe(x => x.BoardChapter == null && x.ChapterTitle == x.Name);
        board.Chapters.Select(x => x.SliceType).Distinct().OrderBy(x => x).ShouldBe(["Command", "View"]);

        var events = board.Chapters.SelectMany(x => x.Steps).Where(x => x.Kind == EmlangElementKind.Event).ToList();
        events.ShouldNotBeEmpty();
        events.Select(x => x.Stream).Distinct().OrderBy(x => x).ShouldBe(["Cart", "Inventory", "Pricing"]);

        // Every declared field type reads as a sketch the stub writer knows; triggeredBy is the processor, not a field
        var fields = board.Chapters.SelectMany(x => x.Steps).SelectMany(x => x.Props)
            .Where(x => x.Key != "triggeredBy").Select(x => x.Value).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        fields.ShouldBe(["List<string>", "double", "int", "string", "uuid"]);
    }

    [Fact]
    public void the_eventsourcing_book_board_imports_with_its_streams_declared_not_inferred()
    {
        var board = EventModelersJsonReader.Read(File.ReadAllText(emspec(Path.Combine("eventsourcing-book", "config.json"))));
        var result = EmlangImport.ToCurated(board, "Cart");

        result.Model.Slices.Count(x => x.Pattern == "Command").ShouldBe(8);
        result.Model.Slices.Count(x => x.Pattern == "View").ShouldBe(5);
        result.Model.EventStreams.Values.Distinct().OrderBy(x => x).ShouldBe(["Cart", "Inventory", "Pricing"]);
        result.Callouts.ShouldNotContain(x => x.Code == ImportCalloutCodes.InferredAgainst || x.Code == ImportCalloutCodes.InferredStreamStart);
        result.Callouts.ShouldNotContain(x => x.Code == ImportCalloutCodes.MissingAggregate);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    /// <summary>What YamlDotNet read, as JSON: every key a string, every scalar the text it was written as.</summary>
    private static JsonNode? toJson(object? value) => value switch
    {
        null => null,
        Dictionary<object, object> map => new JsonObject(map.Select(x => KeyValuePair.Create(x.Key.ToString()!, toJson(x.Value)))),
        List<object> list => new JsonArray(list.Select(toJson).ToArray()),
        _ => JsonValue.Create(value.ToString()!)
    };
}
