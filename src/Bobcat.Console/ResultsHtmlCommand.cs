using Bobcat.Ui.ResultsPages;
using JasperFx.CommandLine;

namespace Bobcat.Console;

public class ResultsHtmlInput
{
    [Description("Results files, or directories holding them (BOBCAT_RESULTS, bobcat run --results); all of one run's files make one page")]
    public IEnumerable<string> Paths { get; set; } = [];

    [Description("Where to write the page; results.html in the first directory given, or beside the first file, by default")]
    public string? OutFlag { get; set; }
}

/// <summary>
/// <c>bobcat results-html</c> — the HTML results page (bobcat#541) for results files already
/// written: a plain <c>dotnet test</c> with <c>BOBCAT_RESULTS</c> set, a supervised run's one file
/// per worker, or a CI artifact downloaded later. <c>bobcat run --html</c> is the same page for the
/// run it just did.
/// </summary>
[Description("Write the self-contained HTML results page for a run's JSON results files", Name = "results-html")]
public class ResultsHtmlCommand : JasperFxCommand<ResultsHtmlInput>
{
    public ResultsHtmlCommand()
    {
        Usage("Write the page for these results files or directories").Arguments(x => x.Paths);
    }

    public override bool Execute(ResultsHtmlInput input)
    {
        var paths = input.Paths.ToList();
        var missing = paths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            System.Console.Error.WriteLine($"No such file or directory: {string.Join(", ", missing)}");
            return false;
        }

        var files = ResultsPage.ResultsFilesIn(paths);
        if (files.Count == 0)
        {
            System.Console.Error.WriteLine($"No results files in {string.Join(", ", paths)}. Write them with BOBCAT_RESULTS=<dir> or `bobcat run --results <dir>`.");
            return false;
        }

        var template = ResultsPage.Template();
        if (template is null)
        {
            System.Console.Error.WriteLine(ResultsPage.NotEmbeddedMessage);
            return false;
        }

        var output = input.OutFlag ?? Path.Combine(
            Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(Path.GetFullPath(paths[0]))!,
            ResultsPage.DefaultFileName);

        try
        {
            var written = ResultsPage.Write(template, output, files);
            System.Console.WriteLine($"Wrote {written} ({files.Count} results file{(files.Count == 1 ? "" : "s")})");
            return true;
        }
        catch (System.Text.Json.JsonException e)
        {
            System.Console.Error.WriteLine($"A results file could not be read as JSON: {e.Message}");
            return false;
        }
    }
}
