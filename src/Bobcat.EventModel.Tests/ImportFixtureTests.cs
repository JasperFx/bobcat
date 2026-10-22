using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Bobcat.EventModel.Emlang;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// bobcat#760: the import's output, kept. Each scenario runs <see cref="EventModelImport"/> — what
/// <c>import-event-model</c> runs — over a board in <c>Fixtures/Import/boards</c> and compares every file it
/// writes, plus its callouts and report as <c>import.json</c>, with the committed tree under
/// <c>Fixtures/Import/generated/{scenario}</c>. <c>docs/bobcat-tool.md</c> includes those files, so the page
/// shows what the import writes rather than what someone remembered it writing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The committed files may carry <c>// #region name</c> lines</b> the import does not write, so the page
/// can include part of a file. The comparison ignores them.
/// </para>
/// <para>
/// <b>When the output changes on purpose</b>, run with <c>BOBCAT_UPDATE_IMPORT_FIXTURES=1</c>: the tree is
/// rewritten from this run, each region line put back above (or, for an end, below) the line it marked.
/// A region whose line no longer exists is dropped, and the docs include that names it then fails on the
/// dev server, which is the prompt to move it. Review the diff; it is what the docs now say.
/// </para>
/// </remarks>
public class ImportFixtureTests
{
    private sealed record Scenario(string Name, string Model, string[] Boards, string[]? Chapters = null, bool StrongTypedIds = false);

    private static readonly Scenario[] scenarios =
    [
        // The page's opening example: one chapter, a command, an automation and a view
        new("k9crush", "K9Crush", ["k9crush.yaml"]),

        // Inferred aggregates and the chapter's default, imported without the chapter that emits
        // ApplicationReviewed, so RequestHomeCheck asks whether it starts its stream
        new("shelter", "Shelter", ["shelter.yaml"], Chapters: ["Volunteering", "Booking"]),

        // --strong-typed-ids
        new("shop", "Shop", ["shop.yaml"], StrongTypedIds: true),

        // A re-import: the second board gives an existing slice a new event
        new("noshow", "Shelter", ["noshow-v1.yaml", "noshow-v2.yaml"]),
    ];

    public static TheoryData<string> Scenarios => new(scenarios.Select(x => x.Name));

    private static readonly Regex regionLine = new(@"^\s*// #(end)?region [\w-]+\s*$");

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void the_import_writes_what_the_docs_show(string name)
    {
        var scenario = scenarios.Single(x => x.Name == name);
        var expected = Path.Combine(fixtures(), "generated", name);
        var actual = Directory.CreateTempSubdirectory("bobcat-import-").FullName;

        try
        {
            EventModelImportResult? last = null;
            foreach (var board in scenario.Boards)
            {
                var yaml = File.ReadAllText(Path.Combine(fixtures(), "boards", board));
                last = EventModelImport.Run(EmlangReader.Read(yaml), actual, new EventModelImportOptions(
                    scenario.Model, Chapters: scenario.Chapters, StrongTypedIds: scenario.StrongTypedIds));
            }

            // Of the last import, which for a re-import is the one that merged. A merge callout names its
            // file by full path, which here is a fresh temp directory, so it is kept relative to the output
            var json = ImportJson.Write(last!.Import.Model.Model, last.Callouts, last.Report)
                .Replace(actual.TrimEnd('/', '\\') + "/", "");
            File.WriteAllText(Path.Combine(actual, "import.json"), json + "\n");

            if (Environment.GetEnvironmentVariable("BOBCAT_UPDATE_IMPORT_FIXTURES") == "1")
            {
                update(actual, expected);
                return;
            }

            compare(actual, expected);
        }
        finally
        {
            Directory.Delete(actual, recursive: true);
        }
    }

    private static void compare(string actual, string expected)
    {
        var written = filesUnder(actual);
        var kept = filesUnder(expected);

        written.ShouldBe(kept, ignoreOrder: true,
            $"the import writes a different set of files than {expected} holds. If that is intended, rerun with BOBCAT_UPDATE_IMPORT_FIXTURES=1");

        foreach (var file in written)
        {
            var output = normalized(File.ReadAllText(Path.Combine(actual, file)));
            var committed = normalized(withoutRegions(File.ReadAllText(Path.Combine(expected, file))));

            output.ShouldBe(committed,
                $"{file} differs from the committed copy, which docs/bobcat-tool.md includes. If the change is intended, rerun with BOBCAT_UPDATE_IMPORT_FIXTURES=1 and review the diff");
        }
    }

    private static void update(string actual, string expected)
    {
        var previous = Directory.Exists(expected)
            ? filesUnder(expected).ToDictionary(x => x, x => File.ReadAllText(Path.Combine(expected, x)))
            : new Dictionary<string, string>();

        if (Directory.Exists(expected)) Directory.Delete(expected, recursive: true);

        foreach (var file in filesUnder(actual))
        {
            var content = normalized(File.ReadAllText(Path.Combine(actual, file)));
            if (previous.TryGetValue(file, out var old)) content = withRegionsOf(normalized(old), content);

            var path = Path.Combine(expected, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }

    // A region line goes back above the line it preceded; an end region below the line it followed
    private static string withRegionsOf(string old, string fresh)
    {
        var oldLines = old.Split('\n');
        var lines = fresh.Split('\n').ToList();
        var cursor = 0;

        for (var i = 0; i < oldLines.Length; i++)
        {
            if (!regionLine.IsMatch(oldLines[i])) continue;

            var isEnd = oldLines[i].Contains("#endregion");
            var anchor = isEnd
                ? oldLines.Take(i).LastOrDefault(x => !regionLine.IsMatch(x))
                : oldLines.Skip(i + 1).FirstOrDefault(x => !regionLine.IsMatch(x));

            // Regions come in file order, so each anchor is looked for after the last one placed
            var at = anchor is null ? -1 : lines.IndexOf(anchor, isEnd ? Math.Max(cursor - 1, 0) : cursor);
            if (at < 0) continue;

            var insertAt = isEnd ? at + 1 : at;
            lines.Insert(insertAt, oldLines[i]);
            cursor = insertAt + 1;
        }

        return string.Join('\n', lines);
    }

    private static string withoutRegions(string text)
        => string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Where(x => !regionLine.IsMatch(x)));

    private static string normalized(string text) => text.Replace("\r\n", "\n");

    private static List<string> filesUnder(string directory)
        => Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Select(x => Path.GetRelativePath(directory, x).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

    private static string fixtures([CallerFilePath] string source = "")
        => Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "Import");
}
