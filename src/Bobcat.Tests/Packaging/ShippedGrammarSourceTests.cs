using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;

namespace Bobcat.Tests.Packaging;

/// <summary>
/// The grammars Bobcat core declares also travel as <b>source</b> in the <c>Bobcat</c> package, because
/// VS Code's Cucumber extension and Rider read step definitions from source only: a step declared solely
/// in a referenced assembly is "undefined" in a consumer's editor. The generator never needs the source;
/// it reads base-fixture steps from assembly metadata.
/// </summary>
/// <remarks>
/// Checked against <c>Bobcat.csproj</c> rather than a packed nupkg so it runs in every build: the pack
/// itself is <c>./build.sh Pack</c>, which CI runs only on publish. The scan over <c>src/Bobcat</c> is
/// what keeps the list honest — a new file declaring steps fails here until it ships as source or is
/// named below as a known exception.
/// </remarks>
public class ShippedGrammarSourceTests
{
    // Not shipped as source today, and recorded as such in docs/editor-integration.md
    private static readonly string[] knownExceptions = ["ClockGrammars.cs"];

    private static readonly Regex stepAttribute =
        new(@"^\s*\[(Given|When|Then|Step|Check)\(""", RegexOptions.Multiline);

    [Theory]
    [InlineData("CritterStack/CritterStackFixture.cs", "contentFiles/cs/any/Bobcat.CritterStack/")]
    [InlineData("CritterStack/DocumentGrammars.cs", "contentFiles/cs/any/Bobcat.CritterStack/")]
    [InlineData("ArrangementSteps.cs", "contentFiles/cs/any/Bobcat/")]
    public void the_grammar_ships_as_source_and_is_never_compiled_by_the_consumer(string file, string contentFiles)
    {
        var items = contentItems(file);

        // contentFiles is what NuGet's PackageReference consumers get; buildAction None keeps the file
        // out of the consumer's compilation, where it would collide with the type in Bobcat.dll
        items.Count(x => x.packagePath == contentFiles).ShouldBe(1, $"{file} is not packed under {contentFiles}");
        var source = items.Single(x => x.packagePath == contentFiles);
        source.buildAction.ShouldBe("None");
        source.copyToOutput.ShouldBe("false");

        // content/grammars is the stable path a consumer points cucumber.glue at
        items.ShouldContain(x => x.packagePath == "content/grammars/" && x.buildAction == "None",
            $"{file} is not packed under content/grammars/");
    }

    [Fact]
    public void every_core_file_declaring_steps_ships_as_source()
    {
        var root = Path.Combine(repositoryRoot(), "src", "Bobcat");
        var declaring = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !x.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(x => stepAttribute.IsMatch(File.ReadAllText(x)))
            .Select(x => Path.GetRelativePath(root, x).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(x => !knownExceptions.Contains(Path.GetFileName(x)))
            .ToList();

        declaring.ShouldContain("CritterStack/CritterStackFixture.cs");

        var notShipped = declaring.Where(x => !contentItems(x).Any(i => i.packagePath.StartsWith("contentFiles/cs/")))
            .ToList();
        notShipped.ShouldBeEmpty(
            "These files declare Gherkin steps but are not packed as source, so no editor sees their step text: "
            + string.Join(", ", notShipped));
    }

    private static List<(string packagePath, string? buildAction, string? copyToOutput)> contentItems(string file)
    {
        var project = XDocument.Load(Path.Combine(repositoryRoot(), "src", "Bobcat", "Bobcat.csproj"));
        return project.Descendants("Content")
            .Where(x => string.Equals(
                ((string?)x.Attribute("Include"))?.Replace('\\', '/'), file, StringComparison.Ordinal))
            .Where(x => (string?)x.Element("Pack") == "true")
            .Select(x => ((string?)x.Element("PackagePath") ?? "",
                (string?)x.Element("BuildAction"),
                (string?)x.Element("PackageCopyToOutput")))
            .ToList();
    }

    private static string repositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "bobcat.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Could not locate the repository's bobcat.slnx.");
    }
}
