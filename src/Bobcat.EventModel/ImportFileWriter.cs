using Bobcat.EventModel.Emlang;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bobcat.EventModel;

/// <summary>How many files one write wrote fresh, merged into, and kept as they were.</summary>
public sealed record ImportWriteCounts(int Written, int Merged, int Kept);

/// <summary>
/// Writes what <c>import-event-model</c> generated to disk. A file that does not exist is written; with
/// <c>--force</c> every file is overwritten, as it always was; otherwise an existing file is merged into
/// additively (<see cref="ImportMerge"/>): the board's new slices, types and specifications are added and
/// nothing already there is changed. Running it twice changes nothing the second time.
/// </summary>
/// <remarks>
/// Each output directory is indexed once (<see cref="SourceIndex"/>) and kept current as files are
/// written, so a type, slice or specification the user moved to another file is still found, and never
/// declared a second time.
/// </remarks>
public sealed class ImportFileWriter(bool force)
{
    private readonly Dictionary<string, SourceIndex> _indexes = new(StringComparer.Ordinal);

    /// <summary>Every file touched, and how.</summary>
    public List<ImportedFile> Files { get; } = [];

    /// <summary>What was merged and what could not be placed.</summary>
    public List<ImportCallout> Callouts { get; } = [];

    /// <summary>One line per merge or skip, for the text report: "Added 1 slice declaration to …".</summary>
    public List<string> Lines { get; } = [];

    /// <summary>The index of what <paramref name="directory"/> already declares.</summary>
    public SourceIndex IndexOf(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!_indexes.TryGetValue(full, out var index)) _indexes[full] = index = SourceIndex.Scan(full);
        return index;
    }

    /// <summary>The stub files: a missing type is added to an existing file, an existing type never touched.</summary>
    public ImportWriteCounts WriteStubs(string directory, IEnumerable<GeneratedFile> files)
    {
        var index = IndexOf(directory);
        return writeEach(directory, files, (file, path, relative) =>
        {
            if (!File.Exists(path))
            {
                var content = ImportMerge.WithoutDeclaredTypes(file.Content, index, out var skipped);
                foreach (var (type, other) in skipped)
                {
                    Lines.Add($"Left {type} out of {relative}: {Path.GetRelativePath(directory, other)} already declares it.");
                }

                return content is null ? Outcome.Skipped : write(path, content, index);
            }

            var result = ImportMerge.MergeStubs(File.ReadAllText(path), file.Content, path, index);
            return merged(path, relative, result, index, "type", "types");
        });
    }

    /// <summary>
    /// The <c>EventModelDefinition</c> files: a slice the board has and no definition in the project
    /// declares is added to the end of its chapter's <c>Configure</c>, and so is a statement reopening a slice
    /// that is declared, with the roles the board gives it and no declaration says.
    /// </summary>
    public ImportWriteCounts WriteDefinitions(string directory, IEnumerable<GeneratedFile> files, ImportedEventModel model)
        => WriteDefinitions(directory, files, model, null);

    /// <inheritdoc cref="WriteDefinitions(string, IEnumerable{GeneratedFile}, ImportedEventModel)"/>
    /// <param name="boardSlices">
    /// Every slice name on the whole board, which an import of only some chapters must supply so that a slice
    /// the board moved to a chapter left out is not reported as removed; null means <paramref name="model"/>'s.
    /// </param>
    public ImportWriteCounts WriteDefinitions(string directory, IEnumerable<GeneratedFile> files, ImportedEventModel model,
        IReadOnlyCollection<string>? boardSlices)
    {
        var index = IndexOf(directory);
        var board = (boardSlices ?? model.Slices.Select(ImportMerge.SliceName).ToList()).ToHashSet(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);

        var counts = writeEach(directory, files, (file, path, relative) =>
        {
            targets.Add(Path.GetFullPath(path));

            if (!File.Exists(path))
            {
                var content = ImportMerge.WithoutDeclaredSlices(file.Content, index, out var skipped);
                foreach (var slice in skipped) Lines.Add($"Left slice {slice} out of {relative}: another definition already declares it.");
                return content is null ? Outcome.Skipped : write(path, content, index);
            }

            var declared = ImportMerge.DeclaredSlices(CSharpSyntaxTree.ParseText(file.Content, ImportMerge.Parse).GetCompilationUnitRoot())
                .ToHashSet(StringComparer.Ordinal);
            var slices = model.Slices.Where(x => declared.Contains(ImportMerge.SliceName(x))).ToList();

            var result = ImportMerge.MergeDefinition(File.ReadAllText(path), file.Content, slices, path, index, board);
            return merged(path, relative, result, index, "declaration", "declarations");
        });

        // A definition the import wrote once and did not write to now, such as one for a chapter the board no
        // longer has: its slices are compared against the board too. Report only, and never with --force.
        if (!force)
        {
            foreach (var other in index.ImportedDefinitionFiles.Where(x => !targets.Contains(x)).Order(StringComparer.Ordinal))
            {
                Callouts.AddRange(ImportMerge.SlicesRemovedOnBoard(File.ReadAllText(other), other, board));
            }
        }

        return counts;
    }

    /// <summary>
    /// The specification files: an example with no specification gets one in the existing class bound to
    /// its feature, wherever that class is. <c>TestSupport.cs</c> is never merged into, only written when
    /// missing; <c>GlobalUsings.cs</c> gains the namespaces it lacks.
    /// </summary>
    public ImportWriteCounts WriteSpecs(string directory, IEnumerable<GeneratedFile> files)
    {
        var index = IndexOf(directory);
        return writeEach(directory, files, (file, path, relative) =>
        {
            if (file.Path == EmlangSpecWriter.TestSupportFile)
            {
                return File.Exists(path) ? Outcome.Kept : write(path, file.Content, index);
            }

            if (file.Path == EmlangSpecWriter.GlobalUsingsFile)
            {
                return File.Exists(path)
                    ? merged(path, relative, ImportMerge.MergeGlobalUsings(File.ReadAllText(path), file.Content, path), index, "global using", "global usings")
                    : write(path, file.Content, index);
            }

            var features = CSharpSyntaxTree.ParseText(file.Content, ImportMerge.Parse).GetCompilationUnitRoot()
                .DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Select(ImportMerge.FeatureOf).OfType<string>().ToList();

            // Into the class bound to the feature, which may have been moved to another file
            var holders = features.SelectMany(index.FilesWithFeature).Distinct(StringComparer.Ordinal).ToList();
            var target = holders.Contains(Path.GetFullPath(path)) ? Path.GetFullPath(path) : holders.Count == 1 ? holders[0] : null;

            if (target is null && holders.Count > 1)
            {
                Callouts.Add(new ImportCallout(features.FirstOrDefault() ?? "", ImportCalloutCodes.MergeSkippedAmbiguous, [],
                    $"{holders.Count} files hold a class bound to [BobcatFeature(\"{features.FirstOrDefault()}\")] "
                    + $"({string.Join(", ", holders.Select(x => Path.GetRelativePath(directory, x)))}), so where {relative}'s new specifications go is ambiguous.", null)
                    { File = path });
                return File.Exists(path) ? Outcome.Kept : Outcome.Skipped;
            }

            if (target is null)
            {
                if (!File.Exists(path)) return write(path, file.Content, index);
                target = Path.GetFullPath(path);
            }

            var result = ImportMerge.MergeSpecs(File.ReadAllText(target), file.Content, target, index);
            var outcome = merged(target, Path.GetRelativePath(directory, target), result, index, "specification", "specifications");

            // Merged into another file: this path itself was never written
            return target == Path.GetFullPath(path) || outcome != Outcome.Merged ? outcome : Outcome.MergedElsewhere;
        });
    }

    private enum Outcome
    {
        Written,
        Merged,
        MergedElsewhere,
        Kept,
        Skipped
    }

    private ImportWriteCounts writeEach(string directory, IEnumerable<GeneratedFile> files, Func<GeneratedFile, string, string, Outcome> merge)
    {
        int written = 0, merged = 0, kept = 0;
        foreach (var file in files)
        {
            var relative = file.Path.Replace('/', Path.DirectorySeparatorChar);
            var path = Path.Combine(directory, relative);

            if (force)
            {
                // --force keeps its meaning: overwrite, as the import always did
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Content); // UTF-8 without a BOM
                Files.Add(new ImportedFile(path, "write"));
                written++;
                continue;
            }

            switch (merge(file, path, relative))
            {
                case Outcome.Written:
                    Files.Add(new ImportedFile(path, "write"));
                    written++;
                    break;
                case Outcome.Merged:
                case Outcome.MergedElsewhere:
                    // merged() recorded the file it wrote, which may be another than this path
                    merged++;
                    break;
                case Outcome.Kept:
                    Files.Add(new ImportedFile(path, "kept"));
                    kept++;
                    break;
            }
        }

        return new ImportWriteCounts(written, merged, kept);
    }

    private static Outcome write(string path, string content, SourceIndex index)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content); // UTF-8 without a BOM
        index.Update(path, content);
        return Outcome.Written;
    }

    private Outcome merged(string path, string relative, ImportMerge.Result result, SourceIndex index, string singular, string plural)
    {
        Callouts.AddRange(result.Callouts);
        if (result.Content is null) return Outcome.Kept;

        File.WriteAllText(path, result.Content);
        index.Update(path, result.Content);
        Files.Add(new ImportedFile(path, "merge"));
        Lines.Add(result.Description is { } description
            ? $"Added {description} to {relative}."
            : $"Added {result.Added.Count} {(result.Added.Count == 1 ? singular : plural)} to {relative}: {string.Join(", ", result.Added)}.");
        return Outcome.Merged;
    }
}
