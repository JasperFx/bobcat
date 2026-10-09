using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// bobcat#424: the eventmodelers.ai platform's JSON exports read into the same board as emlang.
/// The fixtures are small hand-written stand-ins for the two shapes, not copies of public boards.
/// </summary>
public class EventModelersJsonReaderTests
{
    private const string Config =
        """
        {
          "context": "Shop",
          "slices": [
            {
              "title": "Add Item",
              "sliceType": "STATE_CHANGE",
              "aggregates": ["Cart"],
              "screens": [ { "title": "Cart Page", "fields": [] } ],
              "commands": [
                { "title": "Add Item", "fields": [
                  { "name": "cartId", "type": "UUID", "cardinality": "Single", "idAttribute": true },
                  { "name": "quantity", "type": "Int", "cardinality": "Single" },
                  { "name": "tags", "type": "String", "cardinality": "List" } ] }
              ],
              "events": [
                { "title": "Item Added", "aggregate": "default", "fields": [
                  { "name": "cartId", "type": "UUID", "cardinality": "Single" } ] }
              ],
              "readmodels": [],
              "processors": [],
              "specifications": [
                {
                  "title": "Adds to an empty cart",
                  "given": [],
                  "when": [ { "title": "Add Item", "type": "SPEC_COMMAND",
                              "fields": [ { "name": "cartId", "example": "cart-1" }, { "name": "quantity", "example": "2" }, { "name": "tags" } ] } ],
                  "then": [ { "title": "Item Added", "type": "SPEC_EVENT",
                              "fields": [ { "name": "cartId", "example": "cart-1" } ] } ]
                },
                {
                  "title": "Too many",
                  "given": [],
                  "when": [ { "title": "Add Item", "type": "COMMAND", "fields": [ { "name": "quantity", "example": "99" } ] } ],
                  "then": { "title": "Error-Case", "type": "SPEC_ERROR", "description": "Quantity over limit" }
                }
              ]
            }
          ]
        }
        """;

    private const string Board =
        """
        {
          "boards": {},
          "nodes": {},
          "metadata": {
            "board-1": {
              "chapter-1": { "meta": { "type": "CHAPTER", "title": "Initialization", "timelineData": {
                "rows": [
                  { "id": "actor", "type": "actor", "label": "Actor" },
                  { "id": "interaction", "type": "interaction", "label": "Interaction" },
                  { "id": "lane", "type": "swimlane", "label": "System" },
                  { "id": "spec", "type": "spec", "label": "Spec Lane" } ],
                "columns": [ { "id": "c1" }, { "id": "c2" } ],
                "cells": [
                  { "rowId": "actor", "colId": "c1", "nodeId": "auto" },
                  { "rowId": "interaction", "colId": "c1", "nodeId": "cmd" },
                  { "rowId": "lane", "colId": "c1", "nodeId": "evt" },
                  { "rowId": "spec", "colId": "c1", "nodeId": "scn" },
                  { "rowId": "interaction", "colId": "c2", "nodeId": "view" } ] } } },
              "border-1": { "meta": { "type": "SLICE_BORDER", "title": "Initialize", "colId": "c1" } },
              "border-2": { "meta": { "type": "SLICE_BORDER", "title": "Version", "colId": "c2" } },
              "auto": { "meta": { "type": "AUTOMATION", "title": "On Startup" } },
              "cmd": { "meta": { "type": "COMMAND", "title": "Initialize System", "fields": [
                { "name": "version", "type": "Int", "cardinality": "Single" } ] } },
              "evt": { "meta": { "type": "EVENT", "title": "System Initialized", "fields": [
                { "name": "version", "type": "Int", "cardinality": "Single" } ] } },
              "view": { "meta": { "type": "READMODEL", "title": "System Version", "fields": [] } },
              "scn": { "meta": { "type": "SCENARIO", "givenWhenThenScenario": { "scenarios": [
                { "title": "Out of sequence", "expectError": true, "errorDescription": "expected version 1",
                  "given": [], "when": [ { "title": "Initialize System", "type": "COMMAND", "fields": [ { "name": "version", "example": "3" } ] } ],
                  "then": [] } ] } } }
            }
          }
        }
        """;

    [Fact]
    public void the_sniffer_tells_the_json_shapes_from_yaml()
    {
        EventModelFileSniffer.Sniff(Config).ShouldBe(EventModelFileKind.EventModelersConfig);
        EventModelFileSniffer.Sniff(Board).ShouldBe(EventModelFileKind.EventModelersBoard);
        EventModelFileSniffer.Sniff("""{ "something": "else" }""").ShouldBe(EventModelFileKind.Unknown);
        EventModelFileSniffer.Sniff("slices:\n  A:\n    - e: B").ShouldBe(EventModelFileKind.Emlang);
    }

    [Fact]
    public void a_config_slice_is_a_chapter_whose_steps_carry_declared_types()
    {
        var chapter = EventModelersJsonReader.Read(Config).Chapters.Single();

        chapter.Name.ShouldBe("Add Item");
        chapter.Steps.Select(x => (x.Kind, x.Label)).ShouldBe([
            (EmlangElementKind.Screen, "Cart Page"),
            (EmlangElementKind.Command, "Add Item"),
            (EmlangElementKind.Event, "Item Added")
        ]);

        var command = chapter.Steps[1];
        command.Props["cartId"].ShouldBe("uuid");
        command.Props["quantity"].ShouldBe("int");
        command.Props["tags"].ShouldBe("List<string>");
    }

    [Fact]
    public void an_event_whose_aggregate_is_default_takes_the_slices()
    {
        EventModelersJsonReader.Read(Config).Chapters.Single().Steps[2].Actor.ShouldBe("Cart");
    }

    [Fact]
    public void a_specification_keeps_only_the_fields_with_an_example()
    {
        var test = EventModelersJsonReader.Read(Config).Chapters.Single().Tests[0];

        test.Name.ShouldBe("Adds to an empty cart");
        test.When.Single().Props.ShouldBe(new Dictionary<string, string> { ["cartId"] = "cart-1", ["quantity"] = "2" });
        test.Then.Single().Kind.ShouldBe(EmlangElementKind.Event);
    }

    [Fact]
    public void an_error_item_reads_its_description_and_a_lone_then_object_is_one_item()
    {
        var then = EventModelersJsonReader.Read(Config).Chapters.Single().Tests[1].Then.Single();

        then.Kind.ShouldBe(EmlangElementKind.Error);
        then.Label.ShouldBe("Quantity over limit");
    }

    [Fact]
    public void a_board_chapter_reads_its_columns_into_the_slices_its_borders_name()
    {
        var board = EventModelersJsonReader.Read(Board);

        board.Chapters.Select(x => x.Name).ShouldBe(["Initialize", "Version"]);
        board.Chapters[0].Steps.Select(x => (x.Kind, x.Label)).ShouldBe([
            (EmlangElementKind.Command, "Initialize System"),
            (EmlangElementKind.Event, "System Initialized")
        ]);
        board.Chapters[1].Steps.Single().Kind.ShouldBe(EmlangElementKind.View);
    }

    [Fact]
    public void an_automation_cell_makes_the_command_it_precedes_triggered_by_it()
    {
        var model = EmlangImport.ToCurated(EventModelersJsonReader.Read(Board), "Init").Model;

        var slice = model.Slices.Single(x => x.Name == "InitializeSystem");
        slice.Pattern.ShouldBe("Automation");
        slice.Trigger!.Label.ShouldBe("On Startup");
    }

    [Fact]
    public void a_swimlane_label_is_the_events_stream()
    {
        EventModelersJsonReader.Read(Board).Chapters[0].Steps[1].Actor.ShouldBe("System");
    }

    [Fact]
    public void a_scenario_expecting_an_error_refuses_with_its_description()
    {
        var test = EventModelersJsonReader.Read(Board).Chapters[0].Tests.Single();

        test.Name.ShouldBe("Out of sequence");
        test.Then.Single().ShouldBe(new EmlangRef(EmlangElementKind.Error, "", "expected version 1"), new EmlangRefComparer());
    }

    [Fact]
    public void a_free_form_board_with_no_chapter_timeline_is_refused_saying_what_to_export()
    {
        Should.Throw<EmlangFormatException>(() => EventModelersJsonReader.Read(
                """{ "boards": {}, "nodes": {}, "metadata": { "b": { "x": { "meta": { "type": "COMMAND", "title": "A" } } } } }"""))
            .Message.ShouldContain("Export the board as a config.json");
    }

    [Fact]
    public void a_single_exported_slice_reads_as_one_chapter()
    {
        var board = EventModelersJsonReader.Read(
            """{ "title": "Remove Item", "sliceType": "STATE_CHANGE", "commands": [ { "title": "Remove Item", "fields": [] } ], "specifications": [] }""");

        board.Chapters.Single().Name.ShouldBe("Remove Item");
    }

    [Fact]
    public void a_board_with_no_slice_borders_starts_a_slice_at_each_command_or_view()
    {
        var json = Board.Replace("\"SLICE_BORDER\"", "\"NOTHING\"");

        EventModelersJsonReader.Read(json).Chapters.Select(x => x.Name).ShouldBe(["Initialize System", "System Version"]);
    }

    [Fact]
    public void the_generator_writes_specs_from_a_json_export()
    {
        var board = EventModelersJsonReader.Read(Config);
        var model = EmlangImport.ToCurated(board, "Shop").Model;

        var specs = EmlangSpecWriter.Write(board, model, "Shop");

        specs.Specs.ShouldBe(2);
        specs.AllCode().ShouldContain("ThenRefusedWith(\"Quantity over limit\");");
        specs.AllCode().ShouldContain("await WhenReceived(Specify<AddItem>().With(x => x.CartId, theCart).With(x => x.Quantity, 2));");
    }

    private sealed class EmlangRefComparer : IEqualityComparer<EmlangRef>
    {
        public bool Equals(EmlangRef? x, EmlangRef? y) => x is not null && y is not null && x.Kind == y.Kind && x.Actor == y.Actor && x.Label == y.Label;
        public int GetHashCode(EmlangRef obj) => HashCode.Combine(obj.Kind, obj.Label);
    }
}
