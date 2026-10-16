using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bobcat.Ui.ResultsPages;

/// <summary>
/// The HTML results page (bobcat#541): a run's results files (bobcat#540) inlined into one
/// self-contained page, Storyteller's <c>stresults.htm</c> for Bobcat. CI archives it as a build
/// artifact and someone opens it later from disk, with no server. Live stays the board's job.
/// </summary>
/// <remarks>
/// <para>
/// <b>The page is the board's Vue components, built once into a template</b>
/// (<c>vite.config.results.ts</c>: <c>dist-results/results.html</c>, script and styles inlined),
/// embedded here when the tool is packed with <c>EmbedFrontend=true</c>. Writing a page is a text
/// substitution: the template's results element is replaced with the run's results files, so the
/// tool needs no Node and the page renders a scenario exactly as <c>bobcat ui</c> does.
/// </para>
/// <para>
/// <b>Opt-in only</b> — <c>bobcat run --html</c> or <c>bobcat results-html</c>. Nothing writes a
/// page by default; the JSON file is the default-capable artifact and the source of this one.
/// </para>
/// <para>
/// <b>Attachments stay files.</b> A results file names each attachment by a path relative to its
/// own directory; the page links to it through that directory's path relative to the page, so an
/// archive that keeps the two together keeps its screenshots. When the directory is not kept, the
/// page names the attachments and links none.
/// </para>
/// </remarks>
public static class ResultsPage
{
    /// <summary>The template's manifest resource name, set by Bobcat.Ui.csproj.</summary>
    public const string ResourceName = "Bobcat.Ui.results.html";

    /// <summary>The element the template carries for the results, replaced whole.</summary>
    public const string Placeholder = """<script type="application/json" id="bobcat-results">{"files":[]}</script>""";

    /// <summary>The file name the page takes when only a directory is given.</summary>
    public const string DefaultFileName = "results.html";

    /// <summary>Why there is no template, said once so every caller agrees.</summary>
    public const string NotEmbeddedMessage =
        "This bobcat was built without its web pages, so it cannot write the HTML results page. "
        + "The packed tool always has them; a build from source needs `-p:EmbedFrontend=true` (and Node).";

    /// <summary>The embedded template, or null in a build without <c>EmbedFrontend</c>.</summary>
    public static string? Template()
    {
        using var stream = typeof(ResultsPage).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The page: <paramref name="template"/> with <paramref name="files"/> inlined. Each file is a
    /// results file's JSON text and the directory its attachments are under, relative to the page
    /// (null when the attachments were not kept).
    /// </summary>
    /// <exception cref="JsonException">A file is not JSON.</exception>
    /// <exception cref="InvalidOperationException">The template has no results element.</exception>
    public static string Render(string template, IEnumerable<ResultsPageFile> files)
    {
        var index = template.IndexOf(Placeholder, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException("The results page template has no results element to fill.");
        }

        var array = new JsonArray();
        foreach (var file in files)
        {
            array.Add(new JsonObject
            {
                ["attachmentBase"] = file.AttachmentBase,
                ["results"] = JsonNode.Parse(file.Json)
            });
        }

        var data = new JsonObject { ["files"] = array }.ToJsonString();

        // `<` only ever appears inside a JSON string, where < means the same character; escaped,
        // nothing in a scenario's text can close the script element early.
        var element = """<script type="application/json" id="bobcat-results">"""
                      + data.Replace("<", "\\u003c", StringComparison.Ordinal)
                      + "</script>";

        return string.Concat(template.AsSpan(0, index), element, template.AsSpan(index + Placeholder.Length));
    }

    /// <summary>
    /// Write the page for <paramref name="resultsFiles"/> to <paramref name="path"/>, linking each
    /// file's attachments relative to the page unless <paramref name="attachmentsKept"/> is false.
    /// Returns the page's full path.
    /// </summary>
    public static string Write(string template, string path, IReadOnlyList<string> resultsFiles, bool attachmentsKept = true)
    {
        var page = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(page)!;

        var files = resultsFiles.Select(file => new ResultsPageFile(
            File.ReadAllText(file),
            attachmentsKept ? AttachmentBase(directory, Path.GetDirectoryName(Path.GetFullPath(file))!) : null));

        var html = Render(template, files);

        Directory.CreateDirectory(directory);
        File.WriteAllText(page, html, new UTF8Encoding(false));
        return page;
    }

    /// <summary>
    /// The results directory as the page sees it: a relative URL ending in <c>/</c>, or empty when
    /// the page sits in it.
    /// </summary>
    public static string AttachmentBase(string pageDirectory, string resultsDirectory)
    {
        var relative = Path.GetRelativePath(pageDirectory, resultsDirectory);
        if (relative == ".") return "";

        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Select(segment => segment == ".." ? segment : Uri.EscapeDataString(segment));
        return string.Join('/', segments) + "/";
    }

    /// <summary>
    /// The results files <paramref name="paths"/> name: a file as itself, a directory as every
    /// results file in it (the schema beside them excluded), in name order.
    /// </summary>
    public static IReadOnlyList<string> ResultsFilesIn(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                files.AddRange(Directory.EnumerateFiles(path, "*.json")
                    .Where(file => !IsSchema(file))
                    .Order(StringComparer.Ordinal));
            }
            else
            {
                files.Add(path);
            }
        }

        return files.Select(Path.GetFullPath).ToList();
    }

    private static bool IsSchema(string file)
        => Path.GetFileName(file).EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One results file's JSON text, and its directory relative to the page (null when not kept).</summary>
public record ResultsPageFile(string Json, string? AttachmentBase);
