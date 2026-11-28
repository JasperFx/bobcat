using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bobcat.EventModel.Emlang;
using Bobcat.EventModel.Esdm;
using Bobcat.EventModel.Scenarios;
using JasperFx.Events.EventModeling;
using Json.Schema;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// bobcat#850: ESDM documents (<c>.esdm.yaml</c>, the native web) lower read-only to the imported model. The
/// repository's own guide model, vendored under <c>Fixtures/Import/vendored/esdm</c>, covers the aggregate path;
/// the DCB, automation and feature documents are written here to the vendored schemas, which they validate against
/// like the guide's. Every vendored file is untrusted data: read, never executed.
/// </summary>
public class EsdmReaderTests
{
    private static string vendored([CallerFilePath] string path = "")
        => Path.Combine(Path.GetDirectoryName(path)!, "Fixtures", "Import", "vendored", "esdm");

    private static IReadOnlyList<string> guide()
        => Directory.GetFiles(Path.Combine(vendored(), "your-first-model"), "*.esdm.yaml").Order(StringComparer.Ordinal).Select(File.ReadAllText).ToList();

    private static readonly Lazy<JsonSchema> core = new(() => JsonSchema.FromText(yamlToJson(File.ReadAllText(Path.Combine(vendored(), "schema", "core-v1.yaml")))));
    private static readonly Lazy<JsonSchema> gwt = new(() => JsonSchema.FromText(yamlToJson(File.ReadAllText(Path.Combine(vendored(), "schema", "given-when-then-v1.yaml")))));

    /// <summary>A library with a DCB over copies on loan, a policy, a process manager, and features for each.</summary>
    private const string Library =
        """
        apiVersion: schema.esdm.io/core/v1
        kind: domain
        name: library
        ---
        apiVersion: schema.esdm.io/core/v1
        kind: bounded-context
        name: lending
        scope:
          domain: library
        ---
        apiVersion: schema.esdm.io/core/v1
        kind: actor
        name: librarian
        scope:
          domain: library
          boundedContext: lending
        type: human
        ---
        apiVersion: schema.esdm.io/core/v1
        kind: aggregate
        name: copy
        description: One physical copy of a book.
        scope:
          domain: library
          boundedContext: lending
        identifiedBy:
          source: state
          field: copyId
        state:
          type: object
          properties:
            copyId: { type: string, format: uuid }
            title: { type: string }
            shelf: { type: string }
          required: [copyId, title]
        invariants:
          - name: on-shelf
            rule: A copy can only be lent while it is on the shelf.
        ---
        apiVersion: schema.esdm.io/core/v1
        kind: command
        name: lend-copy
        description: Lends one copy to a member.
        scope:
          domain: library
          boundedContext: lending
          aggregate: copy
        data:
          type: object
          properties:
            copyId: { type: string, format: uuid }
            memberId: { type: string, format: uuid }
            due: { type: string, format: date }
            notes:
              type: array
              items: { type: string }
          required: [copyId, memberId, due]
        publishes:
          - copy-lent
        actors:
          - librarian
        constraints:
          - name: member-in-good-standing
            rule: The member has no overdue loans.
        ---
        apiVersion: schema.esdm.io/core/v1
        kind: event
        name: copy-lent
        scope:
          domain: library
          boundedContext: lending
          aggregate: copy
        data:
          type: object
          properties:
            copyId: { type: string, format: uuid }
            memberId: { type: string, format: uuid }
            due: { type: string, format: date }
          required: [copyId, memberId, due]
        ---
        apiVersion: schema.esdm.io/core/v1
        kind: dynamic-consistency-boundary
        name: member-loans
        description: Every copy a member has out, across copies.
        scope:
          domain: library
          boundedContext: lending
        identifiedBy:
          - name: member-id
            source: command-payload
            field: memberId
        consults:
          - boundedContext: lending
            aggregate: copy
            event: copy-lent
            criteria: lent to this member and not yet returned
        invariants:
          - name: at-most-five
            rule: A member holds at most five copies at once.
        ---
        apiVersion: schema.esdm.io/core/v1
        kind: command
        name: reserve-copy
        scope:
          domain: library
          boundedContext: lending
          dynamicConsistencyBoundary: member-loans
        data:
          type: object
          properties:
            memberId: { type: string, format: uuid }
            copyId: { type: string, format: uuid }
          required: [memberId, copyId]
        publishes:
          - copy-reserved
        ---
        apiVersion: schema.esdm.io/core/v1
        kind: event
        name: copy-reserved
        scope:
          domain: library
          boundedContext: lending
        data:
          type: object
          properties:
            memberId: { type: string, format: uuid }
            copyId: { type: string, format: uuid }
          required: [memberId, copyId]
        ---
        apiVersion: schema.esdm.io/core/v1
        kind: read-model
        name: loans
        scope:
          domain: library
          boundedContext: lending
        paradigm: tabular
        schema:
          type: array
          items:
            type: object
            properties:
              copyId: { type: string, format: uuid }
              memberId: { type: string, format: uuid }
              due: { type: string, format: date }
            required: [copyId, memberId, due]
        projections:
          - boundedContext: lending
            aggregate: copy
            event: copy-lent
            rule: Append a row.
        ---
        apiVersion: schema.esdm.io/core/v1
        kind: query
        name: list-loans
        scope:
          domain: library
          boundedContext: lending
        readModel: loans
        result:
          type: array
        ---
        apiVersion: schema.esdm.io/core/v1
        kind: policy
        name: remind-when-overdue
        scope:
          domain: library
        deliveryGuarantee: at-least-once
        idempotency:
          owner: self
        handles:
          - boundedContext: lending
            aggregate: copy
            event: copy-lent
        emits:
          - boundedContext: lending
            aggregate: copy
            command: lend-copy
        ---
        apiVersion: schema.esdm.io/core/v1
        kind: process-manager
        name: loan-tracker
        scope:
          domain: library
        deliveryGuarantee: at-most-once
        correlatedBy:
          source: event-field
          field: copyId
        state:
          type: object
        startsWhen:
          - boundedContext: lending
            aggregate: copy
            event: copy-lent
        endsWhen:
          - name: returned
            condition: the copy is back on the shelf
        timers:
          - name: due-date
            at: due
        reactions:
          - when:
              timer: due-date
            rule: remind the member
            emits:
              - boundedContext: lending
                aggregate: copy
                command: lend-copy
        """;

    private const string Features =
        """
        apiVersion: schema.esdm.io/given-when-then/v1
        kind: feature
        name: lending-a-copy
        scope:
          domain: library
          boundedContext: lending
          aggregate: copy
        scenarios:
          - name: a-copy-on-the-shelf-is-lent
            given:
              - event: copy-acquired
                data: { copyId: c1, title: Dune }
            when:
              command: lend-copy
              actor: librarian
              data: { copyId: c1, memberId: m1, due: 2026-12-24 }
            then:
              events:
                - event: copy-lent
                  data: { copyId: c1, memberId: m1, due: 2026-12-24 }
          - name: a-copy-already-out-is-refused
            given:
              - event: copy-lent
                data: { copyId: c1, memberId: m2, due: 2026-12-01 }
            when:
              command: lend-copy
              data: { copyId: c1, memberId: m1, due: 2026-12-24 }
            then:
              rejection:
                invariant: on-shelf
        ---
        apiVersion: schema.esdm.io/given-when-then/v1
        kind: feature
        name: reserving-across-copies
        scope:
          domain: library
          boundedContext: lending
          dynamicConsistencyBoundary: member-loans
        scenarios:
          - name: a-sixth-copy-is-refused
            given:
              - boundedContext: lending
                aggregate: copy
                event: copy-lent
                data: { copyId: c1, memberId: m1, due: 2026-12-24 }
            when:
              command: reserve-copy
              data: { memberId: m1, copyId: c9 }
            then:
              rejection:
                invariant: at-most-five
        ---
        apiVersion: schema.esdm.io/given-when-then/v1
        kind: feature
        name: tracking-a-loan
        scope:
          domain: library
          processManager: loan-tracker
        scenarios:
          - name: the-due-date-reminds
            given:
              - boundedContext: lending
                aggregate: copy
                event: copy-lent
                data: { copyId: c1, memberId: m1, due: 2026-12-24 }
            when:
              timer: due-date
            then:
              emits:
                - boundedContext: lending
                  aggregate: copy
                  command: lend-copy
              ended: true
        ---
        apiVersion: schema.esdm.io/given-when-then/v1
        kind: feature
        name: listing-loans
        scope:
          domain: library
          boundedContext: lending
          readModel: loans
        scenarios:
          - name: one-loan-lists
            given:
              - boundedContext: lending
                aggregate: copy
                event: copy-lent
                data: { copyId: c1, memberId: m1, due: 2026-12-24 }
            when:
              query: list-loans
              parameters: { memberId: m1 }
            then:
              result:
                - { copyId: c1, memberId: m1, due: 2026-12-24 }
        """;

    private static ImportedEventModel library() => EsdmReader.ToCurated([Library, Features], null, "Library").Model;

    [Fact]
    public void the_sniffer_knows_an_esdm_document_and_the_reader_names_the_model_for_the_domain()
    {
        EventModelFileSniffer.Sniff(Library).ShouldBe(EventModelFileKind.Esdm);
        EventModelFileSniffer.Sniff(Features).ShouldBe(EventModelFileKind.Esdm);
        EventModelFileSniffer.Sniff("slices:\n  A:\n    - e: B").ShouldBe(EventModelFileKind.Emlang);

        library().Model.ShouldBe("Library");
    }

    [Fact]
    public void the_hand_written_documents_validate_against_the_vendored_schemas()
    {
        foreach (var document in EmlangReader.Documents(Library))
        {
            var result = core.Value.Evaluate(JsonSerializer.SerializeToElement(toJson(document)), new EvaluationOptions { OutputFormat = OutputFormat.List });
            result.IsValid.ShouldBeTrue($"{document["kind"]} {document["name"]}: {string.Join("; ", result.Details.Where(x => x.Errors is { Count: > 0 }).Select(x => $"{x.InstanceLocation}: {string.Join(", ", x.Errors!.Values)}"))}");
        }

        foreach (var document in EmlangReader.Documents(Features))
        {
            var result = gwt.Value.Evaluate(JsonSerializer.SerializeToElement(toJson(document)), new EvaluationOptions { OutputFormat = OutputFormat.List });
            result.IsValid.ShouldBeTrue($"feature {document["name"]}: {string.Join("; ", result.Details.Where(x => x.Errors is { Count: > 0 }).Select(x => $"{x.InstanceLocation}: {string.Join(", ", x.Errors!.Values)}"))}");
        }
    }

    [Fact]
    public void a_command_against_an_aggregate_is_a_command_slice_in_its_bounded_context()
    {
        var model = library();
        var lend = model.Slices.Single(x => x.Name == "LendCopy");

        lend.Pattern.ShouldBe("Command");
        lend.Chapter.ShouldBe("lending");
        lend.Domain.ShouldBe("library");
        lend.Aggregates.ShouldBe(["Copy"]);
        lend.Events.ShouldBe(["CopyLent"]);
        lend.Trigger.ShouldSatisfyAllConditions(t => t!.Kind.ShouldBe("Human"), t => t!.Label.ShouldBe("librarian"));
        lend.Description.ShouldBe("Lends one copy to a member.");

        // The aggregate's invariants and the command's own constraints are what it can refuse with
        lend.RejectionReasons.ShouldBe(["A copy can only be lent while it is on the shelf.", "The member has no overdue loans."]);

        // Fields from the JSON Schema: format uuid is a Guid, date a DateOnly, an array a list, and required decides optional
        var command = lend.Elements["LendCopy"];
        command.DeclaredFields["copyId"].Type.ShouldBe("Guid");
        command.DeclaredFields["due"].Type.ShouldBe("DateOnly");
        command.DeclaredFields["notes"].ShouldSatisfyAllConditions(f => f.Many.ShouldBeTrue(), f => f.Optional.ShouldBeTrue(), f => f.Type.ShouldBe("string"));
        command.DeclaredFields["memberId"].Optional.ShouldBeFalse();

        // The aggregate's state is its stub, identified as it says
        var copy = lend.Elements["Copy"];
        copy.Identities.ShouldBe(["copyId"]);
        copy.DeclaredFields["shelf"].Optional.ShouldBeTrue();
        model.EventStreams["CopyLent"].ShouldBe("Copy");
    }

    [Fact]
    public void esdm_cannot_say_whether_a_command_starts_its_stream_so_the_import_says_so()
    {
        var result = EsdmReader.ToCurated([Library], null, "Library");

        var callout = result.Callouts.Single(x => x.Code == ImportCalloutCodes.StreamStartUnknown && x.Slice == "LendCopy");
        callout.Override.ShouldBe("--starts-stream LendCopy=Copy");

        // The flag settles it on a reader-lowered model as it does on a board
        var directory = Directory.CreateTempSubdirectory("bobcat-esdm-").FullName;
        try
        {
            var import = EventModelImport.Run(result, directory, new EventModelImportOptions("Library", "Library", [AggregateOverride.ParseStartsStream("LendCopy=Copy")]));
            var lend = import.Import.Model.Slices.Single(x => x.Name == "LendCopy");
            lend.StartsStream.ShouldBe("Copy");
            lend.Aggregates.ShouldBeEmpty();
            import.Callouts.ShouldNotContain(x => x.Code == ImportCalloutCodes.StreamStartUnknown && x.Slice == "LendCopy");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void a_dynamic_consistency_boundary_is_a_view_over_what_it_consults_read_by_its_identifiers()
    {
        var model = library();
        var boundary = model.Slices.Single(x => x.Name == "MemberLoans");
        var reserve = model.Slices.Single(x => x.Name == "ReserveCopy");

        boundary.Pattern.ShouldBe("View");
        boundary.Dcb.ShouldBeTrue();
        boundary.ConsumedEvents.ShouldBe(["CopyLent"]);
        boundary.Elements["MemberLoans"].DeclaredFields["memberId"].ShouldSatisfyAllConditions(f => f.Identity.ShouldBeTrue(), f => f.Tag.ShouldBe("memberId"));

        reserve.Dcb.ShouldBeTrue();
        reserve.Aggregates.ShouldBeEmpty();
        reserve.Reads.Single().ShouldSatisfyAllConditions(
            r => r.Kind.ShouldBe("Projection"), r => r.Type.ShouldBe("MemberLoans"), r => r.Tag.ShouldBe("memberId"), r => r.Member.ShouldBe("memberId"));
        reserve.RejectionReasons.ShouldBe(["A member holds at most five copies at once."]);
        reserve.Elements["ReserveCopy"].DeclaredFields["memberId"].Tag.ShouldBe("memberId");
        model.Tags.ShouldBe(new Dictionary<string, string?> { ["memberId"] = "Guid" });
    }

    [Fact]
    public void read_models_queries_and_automations_lower_to_their_patterns()
    {
        var model = library();

        var loans = model.Slices.Single(x => x.Name == "Loans");
        loans.Pattern.ShouldBe("View");
        loans.ConsumedEvents.ShouldBe(["CopyLent"]);
        loans.Elements["Loans"].DeclaredFields["due"].Type.ShouldBe("DateOnly");

        var list = model.Slices.Single(x => x.Name == "ListLoans");
        list.Pattern.ShouldBe("Query");
        list.ReadsFrom.ShouldBe(["Loans"]);

        var policy = model.Slices.Single(x => x.Name == "RemindWhenOverdue");
        policy.Pattern.ShouldBe("Automation");
        policy.ConsumedEvents.ShouldBe(["CopyLent"]);
        policy.Messages.ShouldBe(["LendCopy"]);
        policy.Trigger!.Kind.ShouldBe("MessageHandler");

        var tracker = model.Slices.Single(x => x.Name == "LoanTracker");
        tracker.Pattern.ShouldBe("Automation");
        tracker.ConsumedEvents.ShouldBe(["CopyLent"]);
        tracker.Messages.ShouldBe(["LendCopy"]);
        tracker.Notes.ShouldContain("Timers: due-date");
    }

    [Fact]
    public void feature_scenarios_lower_to_the_scenario_model_with_the_invariant_as_the_refusal()
    {
        var model = library();
        var lend = model.Slices.Single(x => x.Name == "LendCopy").Specifications!.Scenarios;

        var lent = lend.Single(x => x.Name == "a copy on the shelf is lent").Model!;
        lent.GivenEvents.Single().ShouldSatisfyAllConditions(g => g.Event.ShouldBe("CopyAcquired"), g => g.Values["title"].ShouldBe("Dune"));
        lent.Act.ShouldBeOfType<MessageReceived>().Message.ShouldBe("LendCopy");
        lent.Actor.ShouldBe("librarian");
        lent.Outcomes.Single().ShouldBeOfType<EventEmitted>().ShouldSatisfyAllConditions(e => e.Event.ShouldBe("CopyLent"), e => e.Stream.ShouldBe("Copy"));

        // The rejection names an invariant; the refusal is that invariant's rule, which the slice declares
        var refused = lend.Single(x => x.Name == "a copy already out is refused").Model!;
        refused.Outcomes.Single().ShouldBeOfType<Refusal>().Reason.ShouldBe("A copy can only be lent while it is on the shelf.");
        refused.Rule.ShouldBe("on-shelf");

        var sixth = model.Slices.Single(x => x.Name == "ReserveCopy").Specifications!.Scenarios.Single().Model!;
        sixth.Outcomes.Single().ShouldBeOfType<Refusal>().Reason.ShouldBe("A member holds at most five copies at once.");

        var reminds = model.Slices.Single(x => x.Name == "LoanTracker").Specifications!.Scenarios.Single().Model!;
        reminds.Act.ShouldBeOfType<TimeAdvances>().By.ShouldBe("due-date");
        reminds.Outcomes.Single().ShouldBeOfType<MessageCaused>().Message.ShouldBe("LendCopy");
        reminds.Note.ShouldBe("The process manager ends here.");

        var listed = model.Slices.Single(x => x.Name == "Loans").Specifications!.Scenarios.Single().Model!;
        listed.Act.ShouldBeOfType<ReadModelQueried>().Parameters["memberId"].ShouldBe("m1");
        var rows = listed.Outcomes.Single().ShouldBeOfType<ReadModelOutcome>();
        rows.Shape.ShouldBe(ReadModelShape.Rows);
        rows.Rows.Single()["due"].ShouldBe("2026-12-24");
    }

    [Fact]
    public void the_generated_code_compiles_and_round_trips_through_the_mapper()
    {
        var model = library();
        var output = CSharpModelWriter.Write(model, "Library", null, perChapter: true);

        CSharpModelWriterTests.compile(output).ShouldBeEmpty();
        var definitions = string.Join("\n", output.DefinitionFiles.Select(x => x.Content));
        definitions.ShouldContain("model.Command<LendCopy>()");
        definitions.ShouldContain(".Against<Copy>()");
        definitions.ShouldContain(""".Refuses("A copy can only be lent while it is on the shelf.")""");
        definitions.ShouldContain(""".Reads<MemberLoans>()""");
        definitions.ShouldContain(""".By("memberId", "MemberId")""");
        definitions.ShouldContain("""model.Query("ListLoans")""");
        definitions.ShouldContain("""model.Automation("RemindWhenOverdue")""");

        var built = CSharpModelWriterTests.buildAll(output).SelectMany(x => x.Model.Slices).ToList();
        var mapped = ImportedModelMapper.ToDescriptor(model, output.Layout, output.Shapes);
        built.Select(x => x.Name).ShouldBe(mapped.Slices.Select(x => x.Name), ignoreOrder: true);
        foreach (var slice in built)
        {
            var expected = mapped.Slices.Single(x => x.Name == slice.Name);
            slice.Pattern.ShouldBe(expected.Pattern, slice.Name);
            slice.RejectionReasons.ShouldBe(expected.RejectionReasons, slice.Name);
            slice.Reads.Count.ShouldBe(expected.Reads.Count, slice.Name);
            slice.ConsumedEvents.Select(x => x.Name).ShouldBe(expected.ConsumedEvents.Select(x => x.Name), ignoreOrder: true, slice.Name);
        }

        // The boundary of the DCB-scoped command is derived from its read of the boundary's view
        var reserve = built.Single(x => x.Name == "ReserveCopy");
        var descriptor = new EventModelDescriptor("Library", built);
        descriptor.BoundaryQueryFor(reserve).Terms.Single().ShouldSatisfyAllConditions(
            t => t.Tag.ShouldBe("memberId"),
            t => t.EventTypes.Select(x => x.Name).ShouldBe(["CopyLent"]));
    }

    // ---- the guide model -----------------------------------------------------------------------

    [Fact]
    public void the_guide_documents_validate_against_the_core_schema()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(vendored(), "your-first-model"), "*.esdm.yaml"))
        {
            foreach (var document in EmlangReader.Documents(File.ReadAllText(file)))
            {
                var result = core.Value.Evaluate(JsonSerializer.SerializeToElement(toJson(document)), new EvaluationOptions { OutputFormat = OutputFormat.List });
                result.IsValid.ShouldBeTrue($"{Path.GetFileName(file)}: {string.Join("; ", result.Details.Where(x => x.Errors is { Count: > 0 }).Select(x => $"{x.InstanceLocation}: {string.Join(", ", x.Errors!.Values)}"))}");
            }
        }
    }

    [Fact]
    public void the_guide_model_imports_from_its_seven_files_to_stubs_that_compile()
    {
        var result = EsdmReader.ToCurated(guide(), null, "Library");
        var model = result.Model;

        model.Model.ShouldBe("Library");
        model.Slices.Select(x => (x.Name, x.Pattern)).ShouldBe([("Acquire", "Command"), ("Books", "View"), ("ListBooks", "Query")]);
        var acquire = model.Slices.Single(x => x.Name == "Acquire");
        acquire.Aggregates.ShouldBe(["Book"]);
        acquire.Events.ShouldBe(["Acquired"]);
        acquire.Chapter.ShouldBe("cataloging");
        acquire.Elements["Book"].Identities.ShouldBe(["isbn"]);
        model.Slices.Single(x => x.Name == "Books").ConsumedEvents.ShouldBe(["Acquired"]);
        result.Report.Last().ShouldContain("Not lowered");

        var output = CSharpModelWriter.Write(model, "Library", null, perChapter: true);
        CSharpModelWriterTests.compile(output).ShouldBeEmpty();
        output.AllStubs().ShouldContain("public record Acquire(string Title, string Author, string Isbn);");
    }

    [Fact]
    public void a_directory_of_esdm_files_is_one_model_for_the_command()
    {
        var files = Bobcat.EventModel.Tests.EsdmReaderTestsSupport.EsdmFiles(Path.Combine(vendored(), "your-first-model"));
        files.Count.ShouldBe(7);
        files.ShouldAllBe(x => x.EndsWith(".esdm.yaml", StringComparison.Ordinal));
    }

    // ---- yaml → json, for the schemas --------------------------------------------------------------

    private static string yamlToJson(string yaml)
        => JsonSerializer.Serialize(toJson(EmlangReader.Documents(yaml).Single()));

    private static JsonNode? toJson(object? value) => value switch
    {
        null => null,
        Dictionary<object, object> map => new JsonObject(map.Select(x => KeyValuePair.Create(x.Key.ToString()!, toJson(x.Value)))),
        List<object> list => new JsonArray(list.Select(toJson).ToArray()),
        string text when text is "true" or "false" => JsonValue.Create(text == "true"),
        string text when long.TryParse(text, out var number) => JsonValue.Create(number),
        _ => JsonValue.Create(value.ToString()!)
    };
}

/// <summary>What the command does with a directory: every <c>*.esdm.yaml</c> under it, in name order (mirrors the command's own rule).</summary>
internal static class EsdmReaderTestsSupport
{
    public static IReadOnlyList<string> EsdmFiles(string path)
        => Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.esdm.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList()
            : [path];
}
