using Bobcat.EventModel.Emlang;

namespace Bobcat.EventModel;

/// <summary>What <c>import-event-model</c> was asked for, past the board itself.</summary>
/// <param name="Model">The model name, which is also the default namespace.</param>
/// <param name="Namespace">The namespace for the stubs and definitions, or null for the model name.</param>
/// <param name="Overrides"><c>--aggregate</c> and <c>--starts-stream</c>, which win over the inference.</param>
/// <param name="Chapters"><c>--chapter</c>: import only these; empty imports every chapter.</param>
/// <param name="SingleDefinition"><c>--single-definition</c>: one definition for the model instead of one per chapter.</param>
/// <param name="StrongTypedIds"><c>--strong-typed-ids</c>: an identity type per aggregate.</param>
/// <param name="Force"><c>--force</c>: overwrite what exists instead of merging into it.</param>
public sealed record EventModelImportOptions(
    string Model,
    string? Namespace = null,
    IReadOnlyList<AggregateOverride>? Overrides = null,
    IReadOnlyList<string>? Chapters = null,
    bool SingleDefinition = false,
    bool StrongTypedIds = false,
    bool Force = false);

/// <summary>Everything one import decided and wrote, for the command's report and its <c>--json</c>.</summary>
public sealed record EventModelImportResult(
    EmlangImportResult Import,
    StrongTypedIds Identities,
    CSharpModelWriter.Output Generated,
    IReadOnlyList<GeneratedFile> DefinitionFiles,
    ImportFileWriter Files,
    ImportWriteCounts Stubs,
    ImportWriteCounts Definitions)
{
    /// <summary>Every callout, in the order the command reports them: the segmentation's, the identities', the merge's.</summary>
    public IReadOnlyList<ImportCallout> Callouts
        => Import.Callouts.Concat(Identities.Callouts).Concat(Files.Callouts).ToList();

    /// <summary>The text report: the segmentation, then a line per file merged into.</summary>
    public IReadOnlyList<string> Report => Import.Report.Concat(Files.Lines).ToList();
}

/// <summary>
/// The whole of <c>import-event-model</c> past reading the file: segment the board, write the C#, and merge
/// it into what <paramref name="outDirectory"/> already holds. The command is this plus its console report
/// and the <c>--url</c> push, so a test of this is a test of what the command writes (bobcat#760's golden
/// files, which the docs include).
/// </summary>
public static class EventModelImport
{
    /// <exception cref="ArgumentException">A <c>--chapter</c> names no chapter on the board (<c>ParamName</c> "chapters").</exception>
    public static EventModelImportResult Run(EmlangBoard board, string outDirectory, EventModelImportOptions options)
    {
        var chapters = options.Chapters ?? [];
        var result = EmlangImport.ToCurated(board, options.Model, options.Namespace, options.Overrides ?? [], chapters);

        // A slice the board moved to a chapter left out is still on the board, so an import of some chapters
        // compares the definitions against the whole board's slice names (slice-removed-on-board)
        var boardSlices = chapters.Count == 0
            ? null
            : EmlangImport.ToCurated(board, options.Model, options.Namespace).Model.Slices.Select(ImportMerge.SliceName).ToList();

        var ns = options.Namespace ?? CSharpModelWriter.Identifiers.Sanitize(options.Model);

        var identities = options.StrongTypedIds
            ? StrongTypedIds.For(result.Model, CSharpModelWriter.StubNames(result.Model))
            : StrongTypedIds.None;

        var generated = CSharpModelWriter.Write(result.Model, ns, null, perChapter: !options.SingleDefinition, identities);

        Directory.CreateDirectory(outDirectory);

        // Nothing is overwritten without --force (bobcat#423): these files are where the design is
        // corrected. An existing file is merged into instead -- the board's new slices and types are
        // added, nothing already there is changed -- so a re-import is additive
        var files = new ImportFileWriter(options.Force);
        var stubs = files.WriteStubs(outDirectory, generated.StubFiles);

        IReadOnlyList<GeneratedFile> definitionFiles = generated.DefinitionFiles.Count > 0
            ? generated.DefinitionFiles
            : [new GeneratedFile($"{options.Model}.cs", generated.Definition)];

        // bobcat#448: one definition per chapter, beside its stubs, each joining the application's model
        var definitions = files.WriteDefinitions(outDirectory, definitionFiles, result.Model, boardSlices);

        return new EventModelImportResult(result, identities, generated, definitionFiles, files, stubs, definitions);
    }
}
