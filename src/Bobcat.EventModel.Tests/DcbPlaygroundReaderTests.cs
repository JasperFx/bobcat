using System.Runtime.CompilerServices;
using System.Text.Json;
using Bobcat.EventModel.Dcb;
using Bobcat.EventModel.Emlang;
using Bobcat.EventModel.Scenarios;
using JasperFx.Events.EventModeling;
using Json.Schema;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// bobcat#849: the dcb.events Playground model (<c>model/v10</c>) lowers to the DCB roles of jasperfx#1297 and to
/// the scenario model. A small model in the test pins the lowering rule by rule; the nine worked examples from
/// dcb.events, vendored under <c>Fixtures/Import/vendored/dcb</c>, prove it against what the Playground writes.
/// Every file there is untrusted data: read, never executed.
/// </summary>
public class DcbPlaygroundReaderTests
{
    private static string vendored([CallerFilePath] string path = "")
        => Path.Combine(Path.GetDirectoryName(path)!, "Fixtures", "Import", "vendored", "dcb");

    private static string example(string name) => Path.Combine(vendored(), "examples", name);

    public static TheoryData<string> Examples => new(
        Directory.GetFiles(Path.Combine(vendored(), "examples"), "*.dcb.json").Select(Path.GetFileName).OrderBy(x => x).Select(x => x!));

    private static readonly Lazy<JsonSchema> schema = new(() => JsonSchema.FromFile(Path.Combine(vendored(), "dcb-model.schema.json")));

    /// <summary>A course with a capacity and students subscribing to it: the dcb.events opening example, cut down.</summary>
    private const string Course =
        """
        {
          "$schema": "https://dcb.events/schemas/model/v10.json",
          "dcbModelVersion": "10.1",
          "name": "Course subscriptions",
          "customTypeDefinitions": [
            { "name": "CourseId", "schema": { "type": "string" }, "isTag": true },
            { "name": "StudentId", "schema": { "type": "string" }, "isTag": true },
            { "name": "Capacity", "schema": { "type": "integer", "minimum": 1 } }
          ],
          "eventDefinitions": [
            { "name": "CourseDefined", "properties": [
                { "name": "courseId", "propertyType": "CourseId", "isOptional": false, "isList": false },
                { "name": "capacity", "propertyType": "Capacity", "isOptional": false, "isList": false } ],
              "tags": ["courseId"] },
            { "name": "StudentSubscribedToCourse", "properties": [
                { "name": "courseId", "propertyType": "CourseId", "isOptional": false, "isList": false },
                { "name": "studentId", "propertyType": "StudentId", "isOptional": false, "isList": false } ],
              "tags": ["courseId", "studentId"] }
          ],
          "entityDefinitions": [],
          "projectionDefinitions": [
            { "name": "CourseExists", "tags": [ { "name": "courseId", "tagType": "CourseId" } ], "valueType": "boolean", "isList": false,
              "initialValue": false, "handlers": [ { "event": "CourseDefined", "operation": "set", "value": true } ] },
            { "name": "CourseSubscriptionCount", "tags": [ { "name": "courseId", "tagType": "CourseId" } ], "valueType": "integer", "isList": false,
              "initialValue": 0, "handlers": [ { "event": "StudentSubscribedToCourse", "operation": "increment", "value": 1 } ] },
            { "name": "TotalCourses", "tags": [], "valueType": "integer", "isList": false,
              "initialValue": 0, "handlers": [ { "event": "CourseDefined", "operation": "increment", "value": 1 } ] }
          ],
          "commandDefinitions": [
            { "name": "DefineCourse", "feature": "Course management",
              "properties": [
                { "name": "courseId", "propertyType": "CourseId", "isOptional": false, "isList": false },
                { "name": "capacity", "propertyType": "Capacity", "isOptional": false, "isList": false } ],
              "boundary": [ { "alias": "total", "projection": "TotalCourses" } ],
              "conditions": [
                { "leftHandSide": { "projection": "CourseExists", "tags": { "courseId": { "parameterName": "courseId" } } },
                  "predicate": "equals", "rightHandSide": false, "rejection": "Course already exists" } ],
              "publishes": [ { "name": "CourseDefined", "parameters": { "courseId": { "parameterName": "courseId" }, "capacity": { "parameterName": "capacity" } } } ] },
            { "name": "SubscribeStudentToCourse", "feature": "Subscriptions",
              "properties": [
                { "name": "courseId", "propertyType": "CourseId", "isOptional": false, "isList": false },
                { "name": "studentId", "propertyType": "StudentId", "isOptional": false, "isList": false } ],
              "boundary": [],
              "conditions": [
                { "leftHandSide": { "projection": "CourseExists", "tags": { "courseId": { "parameterName": "courseId" } } },
                  "predicate": "equals", "rightHandSide": true, "rejection": "Course does not exist" },
                { "leftHandSide": { "projection": "CourseSubscriptionCount", "tags": { "courseId": { "parameterName": "courseId" } } },
                  "predicate": "lessThan", "rightHandSide": 10, "rejection": "Course is full" } ],
              "publishes": [ { "name": "StudentSubscribedToCourse", "parameters": { "courseId": { "parameterName": "courseId" }, "studentId": { "parameterName": "studentId" } } } ] }
          ],
          "scenarioDefinitions": [
            { "id": "s1", "command": "SubscribeStudentToCourse",
              "given": [ { "event": "CourseDefined", "data": { "courseId": "c1", "capacity": 10 } } ],
              "when": { "arguments": { "courseId": "c1", "studentId": "s1" } },
              "then": { "outcome": "published", "events": [ { "type": "StudentSubscribedToCourse", "data": { "courseId": "c1", "studentId": "s1" } } ] } },
            { "id": "s2", "name": "unknown course", "command": "SubscribeStudentToCourse",
              "given": [],
              "when": { "arguments": { "courseId": "c1", "studentId": "s1" } },
              "then": { "outcome": "rejected", "events": [], "rejection": "Course does not exist" } },
            { "id": "s3", "command": "DefineCourse",
              "given": [ { "event": "CourseDefined", "data": { "courseId": "c1", "capacity": 10 } } ],
              "when": { "arguments": { "courseId": "c1", "capacity": 10 } },
              "then": { "outcome": "published", "events": [] } }
          ],
          "projectionScenarioDefinitions": [
            { "id": "p1", "projection": "CourseSubscriptionCount", "tags": { "courseId": { "tagType": "CourseId", "tagValue": "c1" } },
              "given": [ { "event": "StudentSubscribedToCourse", "data": { "courseId": "c1", "studentId": "s1" } } ], "then": 1 }
          ]
        }
        """;

    private static ImportedEventModel course() => DcbPlaygroundReader.ToCurated(Course, null, "Courses").Model;

    [Fact]
    public void the_sniffer_knows_the_envelope()
    {
        EventModelFileSniffer.Sniff(Course).ShouldBe(EventModelFileKind.DcbPlayground);
        EventModelFileSniffer.Sniff("""{ "$schema": "https://dcb.events/schemas/model/v10.json", "name": "x" }""").ShouldBe(EventModelFileKind.DcbPlayground);
        EventModelFileSniffer.Sniff("""{ "slices": [] }""").ShouldBe(EventModelFileKind.EventModelersConfig);
    }

    [Fact]
    public void tag_types_become_the_models_tag_catalogue_with_their_value_type()
    {
        var model = course();

        model.Model.ShouldBe("CourseSubscriptions");
        model.Tags.ShouldBe(new Dictionary<string, string?> { ["courseId"] = "string", ["studentId"] = "string" });
    }

    [Fact]
    public void a_command_is_a_dcb_slice_in_its_feature_with_tagged_events_reads_and_rejections()
    {
        var model = course();
        var subscribe = model.Slices.Single(x => x.Name == "SubscribeStudentToCourse");

        subscribe.Dcb.ShouldBeTrue();
        subscribe.Pattern.ShouldBe("Command");
        subscribe.Chapter.ShouldBe("Subscriptions");
        subscribe.Events.ShouldBe(["StudentSubscribedToCourse"]);
        subscribe.EventTags["StudentSubscribedToCourse"].ShouldBe(["courseId", "studentId"]);
        subscribe.RejectionReasons.ShouldBe(["Course does not exist", "Course is full"]);

        // Each projection a condition reads, by the tag the command member supplies
        subscribe.Reads.Select(x => (x.Kind, x.Type, x.Tag, x.Member)).ShouldBe([
            ("Projection", "CourseExists", "courseId", "courseId"),
            ("Projection", "CourseSubscriptionCount", "courseId", "courseId")]);
        subscribe.ReadsFrom.ShouldBe(["CourseExists", "CourseSubscriptionCount"]);

        // The command's fields are typed from the value types, a tag field knowing its tag
        var command = subscribe.Elements["SubscribeStudentToCourse"];
        command.DeclaredFields["courseId"].ShouldSatisfyAllConditions(f => f.Type.ShouldBe("string"), f => f.Tag.ShouldBe("courseId"));
        model.Slices.Single(x => x.Name == "DefineCourse").Elements["DefineCourse"].DeclaredFields["capacity"].Type.ShouldBe("int");
    }

    [Fact]
    public void a_projection_with_no_tags_is_an_untagged_projection_of_the_command_that_reads_it()
    {
        var define = course().Slices.Single(x => x.Name == "DefineCourse");

        define.UntaggedProjections.ShouldBe(["TotalCourses"]);
        define.Reads.ShouldAllBe(x => x.Type != "TotalCourses");
    }

    [Fact]
    public void a_projection_is_a_view_slice_folding_the_events_its_handlers_name()
    {
        var count = course().Slices.Single(x => x.Name == "CourseSubscriptionCount");

        count.Pattern.ShouldBe("View");
        count.ReadModels.ShouldBe(["CourseSubscriptionCount"]);
        count.ConsumedEvents.ShouldBe(["StudentSubscribedToCourse"]);
        var readModel = count.Elements["CourseSubscriptionCount"];
        readModel.DeclaredFields["courseId"].ShouldSatisfyAllConditions(f => f.Identity.ShouldBeTrue(), f => f.Tag.ShouldBe("courseId"));
        readModel.DeclaredFields["value"].Type.ShouldBe("int");
    }

    [Fact]
    public void scenarios_lower_to_given_when_then_with_a_refusal_and_with_nothing()
    {
        var model = course();
        var subscribe = model.Slices.Single(x => x.Name == "SubscribeStudentToCourse").Specifications!.Scenarios;

        var happy = subscribe.Single(x => x.Name.StartsWith("publishes", StringComparison.Ordinal)).Model!;
        happy.GivenEvents.Single().ShouldSatisfyAllConditions(
            g => g.Event.ShouldBe("CourseDefined"),
            g => g.Values["capacity"].ShouldBe("10"),
            g => g.Tags.ShouldBe(new Dictionary<string, string> { ["courseId"] = "c1" }));
        happy.Act.ShouldBeOfType<MessageReceived>().Values["studentId"].ShouldBe("s1");
        happy.Outcomes.Single().ShouldBeOfType<EventEmitted>().Tags.ShouldBe(new Dictionary<string, string> { ["courseId"] = "c1", ["studentId"] = "s1" });

        var refused = subscribe.Single(x => x.Name == "unknown course").Model!;
        refused.Refuses.ShouldBeTrue();
        refused.Outcomes.Single().ShouldBeOfType<Refusal>().Reason.ShouldBe("Course does not exist");

        var nothing = model.Slices.Single(x => x.Name == "DefineCourse").Specifications!.Scenarios.Single().Model!;
        nothing.Name.ShouldBe("nothing happens");
        nothing.Outcomes.Single().ShouldBeOfType<NothingEmitted>();

        // The element-naming shape is there too, for the writer that still reads it
        subscribe.Single(x => x.Name == "unknown course").Source.ShouldNotBeNull().Then.Single().Kind.ShouldBe(EmlangElementKind.Error);
    }

    [Fact]
    public void a_projection_scenario_asserts_what_the_projection_folds_to()
    {
        var count = course().Slices.Single(x => x.Name == "CourseSubscriptionCount").Specifications!.Scenarios.Single();

        count.Name.ShouldBe("folds to 1");
        var outcome = count.Model!.Outcomes.Single().ShouldBeOfType<ReadModelOutcome>();
        outcome.Values.ShouldBe(new Dictionary<string, string> { ["courseId"] = "c1", ["value"] = "1" });
        count.Model!.Act.ShouldBeNull();
    }

    [Fact]
    public void the_generated_definition_declares_the_tags_reads_and_refusals_and_round_trips()
    {
        var model = course();
        var output = CSharpModelWriter.Write(model, "Courses", null, perChapter: false);

        output.Definition.ShouldContain("""model.Tag("courseId", typeof(string));""");
        output.Definition.ShouldContain(""".Emits<StudentSubscribedToCourse>()""");
        output.Definition.ShouldContain(""".TaggedBy("courseId")""");
        output.Definition.ShouldContain(""".Reads<CourseExists>()""");
        output.Definition.ShouldContain(""".By("courseId", "CourseId")""");
        output.Definition.ShouldContain(""".UntaggedProjection<TotalCourses>()""");
        output.Definition.ShouldContain(""".Refuses("Course is full")""");

        CSharpModelWriterTests.compile(output).ShouldBeEmpty();

        var fromCode = CSharpModelWriterTests.build(output);
        var fromModel = ImportedModelMapper.ToDescriptor(model, output.Layout, output.Shapes);
        describe(fromCode).ShouldBe(describe(fromModel));

        var subscribe = fromCode.Slices.Single(x => x.Name == "SubscribeStudentToCourse");
        subscribe.RejectionReasons.ShouldBe(["Course does not exist", "Course is full"]);
        subscribe.Reads.Count.ShouldBe(2);

        // The boundary is derived from the reads, never declared: one term per tag, folding the projections' events
        var boundary = fromCode.BoundaryQueryFor(subscribe);
        boundary.Terms.Single().Tag.ShouldBe("courseId");
        boundary.Terms.Single().EventTypes.Select(x => x.Name).ShouldBe(["CourseDefined", "StudentSubscribedToCourse"], ignoreOrder: true);
    }

    [Fact]
    public void the_aggregate_inference_leaves_a_dcb_slice_alone()
    {
        var result = DcbPlaygroundReader.ToCurated(Course, null, "Courses");

        result.Callouts.ShouldBeEmpty();
        result.Model.Slices.ShouldAllBe(x => x.Aggregates.Count == 0 && x.StartsStream == null);
    }

    // ---- the worked examples -------------------------------------------------------------------

    /// <summary>
    /// Where a published model and the Playground's schema disagree, by instance location: the one place the site's
    /// export says something the schema does not admit. Recorded rather than papered over, so a schema revision that
    /// closes it fails this test and the entry goes.
    /// </summary>
    private static readonly Dictionary<string, string> knownDeviations = new(StringComparer.Ordinal)
    {
        // `on CopyReturned => set null`: a handler whose value is null, which HandlerOperand does not admit
        ["library-loans.dcb.json"] = "/projectionDefinitions/1/handlers/1/value",
    };

    [Theory]
    [MemberData(nameof(Examples))]
    public void every_example_validates_against_the_playgrounds_schema(string name)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(example(name)))!;
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };

        if (knownDeviations.TryGetValue(name, out var deviation))
        {
            // As published it fails; with the one deviant value taken out it is the schema's model and nothing else
            schema.Value.Evaluate(JsonSerializer.SerializeToElement(document), options).IsValid.ShouldBeFalse($"{name}: the known deviation at {deviation} no longer fails validation, so drop it from knownDeviations");

            var segments = deviation.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var parent = segments.SkipLast(1).Aggregate(document, (node, segment) => int.TryParse(segment, out var index) ? node![index]! : node![segment]!);
            parent!.AsObject().Remove(segments[^1]).ShouldBeTrue();
        }

        var result = schema.Value.Evaluate(JsonSerializer.SerializeToElement(document), options);
        result.IsValid.ShouldBeTrue($"{name}: {string.Join("; ", result.Details.Where(x => x.Errors is { Count: > 0 }).Select(x => $"{x.InstanceLocation}: {string.Join(", ", x.Errors!.Values)}"))}");
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void every_example_imports_to_stubs_that_compile_with_a_derived_boundary_per_command(string name)
    {
        var json = File.ReadAllText(example(name));
        EventModelFileSniffer.Sniff(json).ShouldBe(EventModelFileKind.DcbPlayground);

        var result = DcbPlaygroundReader.ToCurated(json, null, "Example");
        var model = result.Model;
        model.Slices.ShouldContain(x => x.Pattern == "Command");

        var output = CSharpModelWriter.Write(model, "Example", null, perChapter: true);
        CSharpModelWriterTests.compile(output).ShouldBeEmpty(name);

        var descriptor = ImportedModelMapper.ToDescriptor(model, output.Layout, output.Shapes);
        foreach (var slice in model.Slices.Where(x => x.Pattern == "Command"))
        {
            var declared = descriptor.Slices.Single(x => x.Name == slice.Name);
            declared.Pattern.ShouldBe(SlicePattern.Command);
            declared.AggregateTypes.ShouldBeEmpty();

            // A command that reads anything by tag has a boundary JasperFx can derive; one that reads nothing by tag
            // (a global counter, an untagged projection) has none to derive, which is also the model's answer
            if (slice.Reads.Count > 0) descriptor.BoundaryQueryFor(declared).Terms.ShouldNotBeEmpty($"{name}: {slice.Name}");
            else (slice.UntaggedProjections.Count > 0 || slice.ReadsFrom.Count == 0).ShouldBeTrue($"{name}: {slice.Name} reads nothing by tag");
        }

        // The compiled definitions say the same as the mapper, every slice
        var built = CSharpModelWriterTests.buildAll(output).SelectMany(x => x.Model.Slices).ToList();
        built.Select(x => x.Name).ShouldBe(descriptor.Slices.Select(x => x.Name), ignoreOrder: true);
        foreach (var slice in built)
        {
            var mapped = descriptor.Slices.Single(x => x.Name == slice.Name);
            slice.RejectionReasons.ShouldBe(mapped.RejectionReasons);
            slice.Reads.Count.ShouldBe(mapped.Reads.Count, $"{name}: {slice.Name}");
            slice.EventTags.Select(x => x.Event.Name).ShouldBe(mapped.EventTags.Select(x => x.Event.Name), ignoreOrder: true);
        }
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void every_example_scenario_lowers_to_the_scenario_model(string name)
    {
        var json = File.ReadAllText(example(name));
        using var document = JsonDocument.Parse(json);
        var expected = document.RootElement.GetProperty("scenarioDefinitions").GetArrayLength()
                       + (document.RootElement.TryGetProperty("projectionScenarioDefinitions", out var p) ? p.GetArrayLength() : 0);

        var model = DcbPlaygroundReader.ToCurated(json, null, "Example").Model;
        var scenarios = model.Slices.SelectMany(x => x.Specifications?.Scenarios ?? []).ToList();

        scenarios.Count.ShouldBe(expected, name);
        foreach (var scenario in scenarios)
        {
            scenario.Model.ShouldNotBeNull();
            scenario.Model!.Outcomes.ShouldNotBeEmpty($"{name}: {scenario.Name}");
            scenario.Source.ShouldNotBeNull();
        }

        // A rejected scenario names a reason its command declares
        foreach (var slice in model.Slices.Where(x => x.Pattern == "Command"))
        {
            foreach (var refusal in (slice.Specifications?.Scenarios ?? []).SelectMany(x => x.Model!.Outcomes).OfType<Refusal>())
            {
                slice.RejectionReasons.ShouldContain(refusal.Reason, $"{name}: {slice.Name}");
            }
        }
    }

    private static IReadOnlyList<string> describe(EventModelDescriptor descriptor)
        => descriptor.Slices.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => string.Join(" | ",
                $"slice={x.Name}",
                $"pattern={x.Pattern}",
                $"decider={x.DeciderModel?.Name}",
                $"emits={string.Join(",", x.EmittedEvents.Select(e => e.Name))}",
                $"tags={string.Join(";", x.EventTags.Select(t => $"{t.Event.Name}:{string.Join(",", t.Tags)}"))}",
                $"reads={string.Join(";", x.Reads.OrderBy(r => r.Type.Name).Select(r => $"{r.Kind}:{r.Type.Name}:{r.Tag}:{r.Member}"))}",
                $"readsFrom={string.Join(",", x.ReadsFrom.Select(r => r.Name).OrderBy(n => n))}",
                $"untagged={string.Join(",", x.UntaggedProjections.Select(u => u.Name))}",
                $"refuses={string.Join(";", x.RejectionReasons)}"))
            .Prepend($"tags={string.Join(",", descriptor.Tags.OrderBy(t => t.Name).Select(t => $"{t.Name}:{t.InnerType?.Name}"))}")
            .ToList();
}
