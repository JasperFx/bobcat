using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
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
        => Run(source, options: null, featureFiles);

    /// <summary>
    /// The same, with MSBuild properties the generator reads through its
    /// <c>AnalyzerConfigOptionsProvider</c> — projected assertions are opt-in per project
    /// (<c>BobcatProjectedAssertions</c>), so a test that does not pass it generates no
    /// interceptors at all and any assertion about them passes vacuously (issue #410).
    /// </summary>
    public static RunOutcome Run(
        string source,
        IReadOnlyDictionary<string, string>? options,
        params (string Path, string Content)[] featureFiles)
    {
        var references = referenceSet();
        var compilation = CSharpCompilation.Create(
            "SpecProject",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(
            [new BobcatGenerator().AsSourceGenerator()],
            additionalTexts: featureFiles.Select(f => (AdditionalText)new FeatureText(f.Path, f.Content)),
            parseOptions: parseOptions,
            optionsProvider: options is null ? null : new MsBuildProperties(options));

        var ran = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        var result = ran.GetRunResult();
        return new RunOutcome(result.Diagnostics, result, updated);
    }

    /// <summary>
    /// With C#'s <c>interceptors</c> feature enabled for Bobcat's generated namespace — the
    /// compiler flag a real consumer gets from <c>Bobcat.Generators.props</c>
    /// (<c>InterceptorsNamespaces</c>). Without it every generated interceptor is CS9137 here,
    /// which is a fact about the harness rather than about the generator (issue #410).
    /// </summary>
    private static readonly CSharpParseOptions parseOptions = CSharpParseOptions.Default
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Bobcat.Generated")]);

    private static ImmutableArray<MetadataReference> referenceSet()
    {
        var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = trusted.Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(Fixture).Assembly.Location));

        // Shouldly, so the projected-assertion interceptors can be generated AND COMPILED here
        // (issue #410). Without it, the one failure mode that matters for that feature — an
        // interceptor whose signature does not match its target — is only ever discovered by a
        // consumer's build.
        references.Add(MetadataReference.CreateFromFile(
            typeof(Shouldly.ShouldBeTestExtensions).Assembly.Location));

        return [.. references];
    }

    /// <summary>Global MSBuild properties, as the generator's options provider sees them.</summary>
    private sealed class MsBuildProperties(IReadOnlyDictionary<string, string> values)
        : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(values);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;

        private sealed class Options(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                if (values.TryGetValue(key, out var found))
                {
                    value = found;
                    return true;
                }

                value = null!;
                return false;
            }
        }
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
