using Bobcat.Ui.ResultsPages;
using JasperFx.CommandLine;

namespace Bobcat.Console.LivingDocs;

public class ResultsDocsInput
{
    [Description("Results files, or directories holding them (BOBCAT_RESULTS, bobcat run --results); all of one run's files make one set of pages")]
    public IEnumerable<string> Paths { get; set; } = [];

    [Description("The directory to write the pages into; `docs` under the first directory given, or beside the first file, by default")]
    public string? OutFlag { get; set; }

    [Description("Do not write bobcat-spec.js, the <bobcat-spec> web component, beside the pages")]
    public bool NoScriptFlag { get; set; }
}

/// <summary>
/// <c>bobcat results-docs</c> — living documentation (bobcat#553) from results files already
/// written: one Markdown page per specification, an index per feature and per run, the folded
/// results and the <c>&lt;bobcat-spec&gt;</c> script, for a documentation site to include.
/// <c>bobcat run --docs &lt;dir&gt;</c> writes the same for the run it just did.
/// </summary>
[Description("Write living documentation (Markdown per specification, indexes, and the <bobcat-spec> web component) from a run's JSON results files", Name = "results-docs")]
public class ResultsDocsCommand : JasperFxCommand<ResultsDocsInput>
{
    public ResultsDocsCommand()
    {
        Usage("Write the documentation for these results files or directories").Arguments(x => x.Paths);
    }

    public override bool Execute(ResultsDocsInput input)
    {
        var paths = input.Paths.ToList();
        if (paths.Count == 0)
        {
            System.Console.Error.WriteLine("Name a results file or a directory holding them: bobcat results-docs <paths> --out <dir>. See 'bobcat help results-docs'.");
            return false;
        }

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

        var output = input.OutFlag ?? Path.Combine(
            Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(Path.GetFullPath(paths[0]))!,
            "docs");

        string? script = null;
        if (!input.NoScriptFlag)
        {
            script = SpecEmbed.Script();
            if (script is null) System.Console.Error.WriteLine(SpecEmbed.NotEmbeddedMessage);
        }

        try
        {
            var outcome = ResultsDocs.Write(files, output, attachmentsKept: true, script);
            System.Console.WriteLine(outcome.Describe());
            return true;
        }
        catch (System.Text.Json.JsonException e)
        {
            System.Console.Error.WriteLine($"A results file could not be read as JSON: {e.Message}");
            return false;
        }
        catch (InvalidDataException e)
        {
            System.Console.Error.WriteLine(e.Message);
            return false;
        }
    }
}
