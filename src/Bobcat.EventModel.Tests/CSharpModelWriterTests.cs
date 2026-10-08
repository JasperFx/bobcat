using System.Collections.Immutable;
using System.Reflection;
using Bobcat.EventModel.Emlang;
using JasperFx.Events.EventModeling;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// Issue #405: the eventmodelers.ai import writes <b>C#</b> — field-less stub records plus one
/// <c>EventModelDefinition</c> declaring the slices through the JasperFx.Events fluent API.
/// </summary>
/// <remarks>
/// <para>
/// <b>These compile the output and run it</b>, rather than asserting on the emitted text. The one
/// failure a scaffolder must never have is output that does not build, and a text assertion passes
/// happily for a file with a missing brace, a class shadowing its own namespace, or a generic role
/// call naming a type no stub declares — which are the three mistakes the first cut actually made,
/// all three invisible to any assertion about substrings.
/// </para>
/// <para>
/// So the shape is: emit → compile in memory → load → <c>Configure</c> → compare the descriptor to
/// the one <c>ImportedModelMapper</c> builds from the same model. That second half is what makes it
/// a round-trip rather than a smoke test: it says the generated code <em>means</em> what the board
/// said, not merely that it parses.
/// </para>
/// </remarks>
public class CSharpModelWriterTests
{
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
            tests:
              ALikeIsRecorded:
                given: [{ e: Member/Dog Liked }]
                when: [{ c: Member/Swipe On Dog }]
                then: [{ e: Member/Dog Liked }]
        """;

    private static ImportedEventModel imported(string yaml = SwipeChapter, string model = "K9Crush")
        => EmlangImport.ToCurated(EmlangReader.Read(yaml), model).Model;

    [Fact]
    public void the_generated_code_compiles()
    {
        var generated = CSharpModelWriter.Write(imported());

        // No diagnostics at all, not merely no errors: a warning in generated code is a warning
        // the consumer cannot fix, and `TreatWarningsAsErrors` is common enough that it would
        // break their build rather than ours.
        compile(generated).ShouldBeEmpty();
    }

    [Fact]
    public void every_stub_the_board_named_is_declared_once()
    {
        var model = imported();
        var generated = CSharpModelWriter.Write(model);

        // Events are field-less: a board carries no field information for them, and inventing one
        // would be a guess every consumer then has to un-guess
        var events = new[] { "DogLiked", "DogPassed", "MutualMatchDetected" };
        foreach (var name in events)
        {
            generated.AllStubs().ShouldContain($"public record {name};");
        }

        // Commands and read models the model names no identity for are identified by Id, the
        // Wolverine convention (bobcat#438), so a specification can address their stream
        foreach (var name in new[] { "SwipeOnDog", "DetectMutualMatch", "MatchList" })
        {
            generated.AllStubs().ShouldContain($"public record {name}(Guid Id);");
        }

        // The streams those events are on, inferred from what they are about (bobcat#444): classes,
        // because an aggregate is the write model the store folds
        foreach (var name in new[] { "Dog", "MutualMatch" })
        {
            generated.AllStubs().ShouldContain($"public class {name} {{ public Guid Id {{ get; set; }} }}");
        }

        generated.StubCount.ShouldBe(events.Length + 3 + 2);
    }

    [Fact]
    public void each_slice_gets_a_file_in_its_chapters_folder_and_namespace()
    {
        // bobcat#441: one file per slice, holding what the slice produces, under Features/{Chapter}
        var generated = CSharpModelWriter.Write(imported());

        var swipe = generated.StubFiles.Single(x => x.Path == "Features/TheSwiper/SwipeOnDog.cs");
        swipe.Content.ShouldContain("namespace K9Crush.TheSwiper;");
        swipe.Content.ShouldContain("public record SwipeOnDog(Guid Id);");
        swipe.Content.ShouldContain("public record DogLiked;");
        swipe.Content.ShouldContain("public record DogPassed;");

        generated.StubFiles.Single(x => x.Path == "Features/TheSwiper/DetectMutualMatch.cs")
            .Content.ShouldContain("public record MutualMatchDetected;");
        generated.StubFiles.Single(x => x.Path == "Features/TheSwiper/MatchList.cs")
            .Content.ShouldContain("public record MatchList(Guid Id);");

        // The definition stays one file in the root namespace and brings every chapter in
        generated.Definition.ShouldContain("using K9Crush.TheSwiper;");
        generated.Definition.ShouldContain("namespace K9Crush;");
    }

    [Fact]
    public void an_identity_the_model_names_means_no_default_id()
    {
        var generated = CSharpModelWriter.Write(imported(
            """
            slices:
              Ordering:
                steps:
                  - c: Customer/Cancel order
                    props: { id: uuid }
                  - e: Customer/Order cancelled
            """));

        generated.AllStubs().ShouldContain("public record CancelOrder(Guid Id);");
        generated.AllStubs().ShouldNotContain("Guid Id, Guid Id");
    }

    [Fact]
    public void the_definition_class_does_not_shadow_its_own_namespace()
    {
        // The model name supplies both the namespace and the class name by default, so
        // `namespace K9Crush { class K9Crush }` was the first cut's output. It compiles — and then
        // every reference to a sibling stub inside it resolves against the CLASS before the
        // namespace, so `K9Crush.SwipeOnDog` stops meaning what it says.
        var generated = CSharpModelWriter.Write(imported());

        generated.Definition.ShouldContain("namespace K9Crush;");
        generated.Definition.ShouldContain("public class K9CrushEventModel : EventModelDefinition");

        // And the suffix is only applied where the collision is real.
        CSharpModelWriter.DefinitionClassName("K9Crush", "Acme.Dogs").ShouldBe("K9Crush");
        CSharpModelWriter.DefinitionClassName("K9Crush", "Acme.K9Crush").ShouldBe("K9CrushEventModel");
    }

    [Fact]
    public void the_model_name_survives_because_it_is_the_merge_key()
    {
        // Both halves of the merge are keyed on the model name, which is why this is load-bearing
        // rather than cosmetic: a definition naming a different model floats off as a second
        // diagram instead of folding into the real one.
        CSharpModelWriter.Write(imported(model: "BankAccountES")).Definition
            .ShouldContain("""public override string Name => "BankAccountES";""");
    }

    [Fact]
    public void the_declared_model_round_trips_through_the_compiled_definition()
    {
        var model = imported();
        var generated = CSharpModelWriter.Write(model);

        var fromCode = describe(build(generated));
        var fromModel = describe(ImportedModelMapper.ToDescriptor(model));

        // The whole claim of #405 in one assertion: the generated C# says what the board said.
        fromCode.ShouldBe(fromModel);
    }

    [Fact]
    public void a_pattern_the_board_knew_reaches_the_descriptor()
    {
        var descriptor = build(CSharpModelWriter.Write(imported()));

        slice(descriptor, "SwipeOnDog").Pattern.ShouldBe(SlicePattern.Command);
        slice(descriptor, "MatchList").Pattern.ShouldBe(SlicePattern.View);

        // The one Gherkin cannot express, which is the board's whole contribution (issue #202).
        slice(descriptor, "DetectMutualMatch").Pattern.ShouldBe(SlicePattern.Automation);
    }

    [Fact]
    public void a_generic_verb_is_only_used_when_it_names_the_slice_the_board_named()
    {
        // There is no call that renames a slice, and the name IS the merge key — so a generic
        // verb taking its name from a type is usable only when the two agree. When they do not,
        // the slice opens by name and states its pattern and role separately, which says the same
        // thing. Getting this wrong splits one slice into two SILENTLY, which is why it is pinned.
        var model = imported();
        var command = model.Slices.First(x => x.Name == "SwipeOnDog");

        command.Name = "RecordASwipe";

        var generated = CSharpModelWriter.Write(model);

        generated.Definition.ShouldNotContain("model.Command<SwipeOnDog>()");
        generated.Definition.ShouldContain("""model.Command("RecordASwipe")""");
        generated.Definition.ShouldContain(".Command<SwipeOnDog>()");

        compile(generated).ShouldBeEmpty();
        slice(build(generated), "RecordASwipe").Pattern.ShouldBe(SlicePattern.Command);
    }

    [Fact]
    public void a_role_naming_a_type_with_no_stub_is_declared_by_name_so_the_output_still_builds()
    {
        // A handler is never stubbed — an empty handler is what the scaffold command exists to
        // write — so it has to be declared by name. The two are equivalent to the merge, because
        // a string becomes a TypeDescriptor whose empty assembly makes comparison fall back to
        // Name; reaching for a generic over a type no stub declares would simply not compile.
        var model = imported();
        model.Slices.First(x => x.Name == "SwipeOnDog").Handler = "SwipeEndpoint";

        var generated = CSharpModelWriter.Write(model);

        generated.Definition.ShouldContain(""".HandledBy("SwipeEndpoint")""");
        generated.AllStubs().ShouldNotContain("public record SwipeEndpoint;");
        compile(generated).ShouldBeEmpty();
    }

    [Fact]
    public void an_empty_domain_is_not_declared_at_all()
    {
        // `.InDomain("")` was in the first cut's output. An empty domain is not a domain, and
        // declaring one puts an unnamed sub-diagram on the canvas.
        CSharpModelWriter.Write(imported()).Definition.ShouldNotContain("""InDomain("")""");
    }

    [Fact]
    public void a_board_label_that_is_not_an_identifier_still_produces_code_that_compiles()
    {
        // Board labels are free text. "2FA Enrolled" leads a record declaration with a digit and
        // a space, neither of which parses, so the sanitizer is what keeps the output buildable.
        var generated = CSharpModelWriter.Write(imported(
            """
            slices:
              Security:
                steps:
                  - c: Member/Enrol in 2FA
                  - e: Member/2FA Enrolled
            """));

        generated.AllStubs().ShouldContain("public record _2FAEnrolled;");
        compile(generated).ShouldBeEmpty();
    }

    [Fact]
    public void a_board_that_names_nothing_still_writes_files_that_compile()
    {
        var generated = CSharpModelWriter.Write(new ImportedEventModel { Schema = 1, Model = "Empty" });

        generated.StubCount.ShouldBe(0);
        compile(generated).ShouldBeEmpty();
    }

    /// <summary>
    /// The roles of every slice, as comparable text. Ordered so the comparison is about content
    /// rather than about the order two different builders happened to add things in.
    /// </summary>
    private static IReadOnlyList<string> describe(EventModelDescriptor descriptor)
        => descriptor.Slices
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => string.Join(
                " | ",
                $"slice={x.Name}",
                $"pattern={x.Pattern}",
                $"domain={x.Domain}",
                $"chapter={x.Chapter}",
                $"trigger={x.TriggerLabel}/{x.TriggerKind}",
                $"command={x.CommandType?.Name}",
                $"handler={x.HandlerType?.Name}",
                $"aggregates={names(x.AggregateTypes)}",
                $"emits={names(x.EmittedEvents)}",
                $"publishes={names(x.PublishedMessages)}",
                $"consumes={names(x.ConsumedEvents)}",
                $"reads={names(x.ReadsFrom)}",
                $"readmodels={names(x.ReadModelTypes)}",
                $"projections={names(x.ProjectionTypes)}",
                $"specs={ordered(x.Specifications.Select(s => s.Identity))}"))
            .ToList();

    private static string names(IReadOnlyList<JasperFx.Descriptors.TypeDescriptor>? types)
        => types is null ? "" : ordered(types.Select(x => x.Name));

    private static string ordered(IEnumerable<string> values)
        => string.Join(",", values.OrderBy(x => x, StringComparer.Ordinal));

    private static EventModelSliceDescriptor slice(EventModelDescriptor descriptor, string name)
        => descriptor.Slices.Single(x => x.Name == name);

    /// <summary>
    /// Compile both generated files together and return every diagnostic.
    /// </summary>
    /// <remarks>
    /// The two files are one compilation on purpose: the definition references the stubs, so
    /// compiling either alone would prove nothing about the pair, and it is the pair an import
    /// writes.
    /// </remarks>
    internal static IReadOnlyList<Diagnostic> compile(CSharpModelWriter.Output generated)
        => compilation(generated).GetDiagnostics()
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .ToList();

    private static CSharpCompilation compilation(CSharpModelWriter.Output generated)
        => CSharpCompilation.Create(
            "ImportedModel" + Guid.NewGuid().ToString("N"),
            generated.StubFiles.Select(x => CSharpSyntaxTree.ParseText(x.Content, path: x.Path))
                .Append(CSharpSyntaxTree.ParseText(generated.Definition)),
            references(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    /// <summary>
    /// Compile, load and run the generated definition, returning the descriptor it declares.
    /// </summary>
    internal static EventModelDescriptor build(CSharpModelWriter.Output generated)
    {
        using var stream = new MemoryStream();
        var emitted = compilation(generated).Emit(stream);

        emitted.Success.ShouldBeTrue(
            "the generated code must compile: "
            + string.Join(
                Environment.NewLine,
                emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        var assembly = Assembly.Load(stream.ToArray());

        var type = assembly.GetTypes().Single(t => typeof(EventModelDefinition).IsAssignableFrom(t));
        var definition = (EventModelDefinition)Activator.CreateInstance(type)!;

        var builder = new EventModelBuilder();
        definition.Configure(builder);

        return builder.Build(definition.Name);
    }

    private static ImmutableArray<MetadataReference> references()
    {
        var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;

        return
        [
            .. trusted.Split(Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        ];
    }
}
