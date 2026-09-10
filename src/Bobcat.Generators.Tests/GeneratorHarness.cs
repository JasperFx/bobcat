using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Bobcat.Generators.Tests;

/// <summary>
/// Runs <see cref="BobcatGenerator"/> over an in-memory compilation — a "real project" both
/// sides of a diagnostic can be written against without a csproj per case. The compilation
/// references the actual Bobcat runtime assembly, so fixture/base-class discovery works exactly
/// as it does under MSBuild.
/// </summary>
public static class GeneratorHarness
{
    public sealed record RunOutcome(
        ImmutableArray<Diagnostic> Diagnostics,
        GeneratorDriverRunResult Result,
        Compilation Compilation)
    {
        public IEnumerable<Diagnostic> WithId(string id) => Diagnostics.Where(d => d.Id == id);

        /// <summary>
        /// Compiler errors from the source PLUS everything the generator emitted. A generator can
        /// produce no diagnostics of its own and still write a file that does not parse, which is
        /// exactly what issue #269 was: `namespace &lt;global namespace&gt;;` and 14 CS errors in a
        /// file the author never wrote and cannot edit.
        /// </summary>
        public IReadOnlyList<Diagnostic> CompilationErrors =>
            Compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

        public string GeneratedSource(string hintContains)
            => Result.Results
                   .SelectMany(r => r.GeneratedSources)
                   .Where(s => s.HintName.Contains(hintContains, StringComparison.OrdinalIgnoreCase))
                   .Select(s => s.SourceText.ToString())
                   .FirstOrDefault()
               ?? throw new InvalidOperationException(
                   $"No generated source matching '{hintContains}'. Generated: " +
                   string.Join(", ", Result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.HintName)));
    }

    public static RunOutcome Run(string source, params (string Path, string Content)[] featureFiles)
    {
        var references = referenceSet();
        var compilation = CSharpCompilation.Create(
            "SpecProject",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(
            [new BobcatGenerator().AsSourceGenerator()],
            additionalTexts: featureFiles.Select(f => (AdditionalText)new FeatureText(f.Path, f.Content)));

        var ran = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        var result = ran.GetRunResult();
        return new RunOutcome(result.Diagnostics, result, updated);
    }

    private static ImmutableArray<MetadataReference> referenceSet()
    {
        var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = trusted.Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(Fixture).Assembly.Location));
        return [.. references];
    }

    private sealed class FeatureText : AdditionalText
    {
        private readonly string _content;

        public FeatureText(string path, string content)
        {
            Path = path;
            _content = content;
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default)
            => SourceText.From(_content, Encoding.UTF8);
    }
}
