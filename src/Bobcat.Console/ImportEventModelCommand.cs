using System.Text;
using System.Text.Json;
using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using JasperFx.CommandLine;

namespace Bobcat.Console;

public class ImportEventModelInput
{
    [Description("An eventmodelers.ai emlang board export")]
    public string FilePath { get; set; } = string.Empty;

    [Description("Model name; defaults to the file name. It is the merge key, so it must match what the code-derived sources call the model")]
    [FlagAlias("model", 'm')]
    public string? ModelFlag { get; set; }

    [Description("Namespace for the generated stubs and definition; defaults to the model name")]
    public string? NamespaceFlag { get; set; }

    [Description("Directory the generated C# is written to; defaults beside the input")]
    [FlagAlias("out", 'o')]
    public string? OutFlag { get; set; }

    [Description("Also write a WolverineFx.Bobcat specification for every example (test) in the model (bobcat#423)")]
    public bool SpecsFlag { get; set; }

    [Description("Overwrite files that already exist. Without it nothing is overwritten, and a second --specs run reports the examples that have no specification")]
    public bool ForceFlag { get; set; }

    [Description("Base URL of a run console to push the assembled model to (e.g. http://localhost:5525)")]
    [FlagAlias("url", 'u')]
    public string? UrlFlag { get; set; }
}

/// <summary>
/// <c>bobcat import-event-model &lt;file&gt;</c> — read an emlang model, segment it, and write the
/// design out as <b>C#</b>: stub records with the fields the model names, plus one
/// <c>EventModelDefinition</c> (issues #202, #405, #422). With <c>--specs</c>, also one
/// WolverineFx.Bobcat specification per example in the model (bobcat#423). Nothing is overwritten
/// without <c>--force</c>; a second <c>--specs</c> run reports the examples with no specification. Optionally pushes the assembled model to a run
/// console, which lives in Stoat since the 2026-09-18 fold; this side is only ever an HTTP client
/// of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It writes code now, not YAML (issue #405).</b> jasperfx#955 settled that YAML is not an
/// authoring syntax — the only YAML Bobcat reads is the platform's own, on import. So the output
/// is the code the design starts from: specs can be written against the stubs immediately, they
/// are red until the behaviour exists, and a Wolverine <c>scaffold</c> command fills in the
/// slices the model declares but the code does not yet implement.
/// </para>
/// <para>
/// <b>The curated arm is gone.</b> This command no longer reads a curated <c>.emodel.yaml</c>; a
/// file in that shape is refused with a sentence saying where to go instead, which is strictly
/// more use than the "unrecognized" it would otherwise get. <c>EmlangReader</c> and
/// <c>EmlangImport</c> are unchanged — only the writer at the end of the pipe changed.
/// </para>
/// <para>
/// Segmentation is still a set of reported guesses, and a wrong guess is still meant to be a
/// one-line edit rather than a re-import — the difference is that the line is now C#, and
/// <b>nothing regenerates the file</b>, so an edit cannot be clobbered. Every decision lives in
/// <c>Bobcat.EventModel</c> and is unit-tested; what remains here is file and HTTP orchestration,
/// kept deliberately thin (the <c>watch-event-model</c> precedent).
/// </para>
/// </remarks>
[Description("Import an eventmodelers.ai board export as C# stubs and an EventModelDefinition, and optionally push it to a console", Name = "import-event-model")]
public class ImportEventModelCommand : JasperFxAsyncCommand<ImportEventModelInput>
{
    public ImportEventModelCommand()
    {
        Usage("Import a board export as C# and summarize the model").Arguments(x => x.FilePath);
    }

    public override async Task<bool> Execute(ImportEventModelInput input)
    {
        if (!File.Exists(input.FilePath))
        {
            System.Console.Error.WriteLine($"No file at {input.FilePath}");
            return false;
        }

        var yaml = await File.ReadAllTextAsync(input.FilePath);

        var curated = EventModelFileSniffer.Sniff(yaml) switch
        {
            EventModelFileKind.Emlang => importEmlang(input, yaml),
            EventModelFileKind.Curated => refuseCurated(input.FilePath),
            _ => describeUnrecognized(input.FilePath),
        };

        if (curated is null) return false;

        var descriptor = ImportedModelMapper.ToDescriptor(curated);
        System.Console.WriteLine(
            $"Model '{descriptor.Name}': {descriptor.Slices.Count} slice(s), "
            + $"{descriptor.Slices.Sum(x => x.Specifications.Count)} bound specification(s).");

        return input.UrlFlag is null || await pushAsync(input.UrlFlag, descriptor);
    }

    /// <summary>
    /// The file exists and is not a board export. Issue #369: this arm used to return null with no
    /// message at all, so the command exited 1 having printed nothing. Naming the shape it wanted
    /// is the whole fix.
    /// </summary>
    private static ImportedEventModel? describeUnrecognized(string path)
    {
        System.Console.Error.WriteLine(
            $"{path} is not an emlang file. Expected a top-level `slices:` map, each slice a list of "
            + "steps or a map of `steps:` and `tests:`. (An eventmodelers.ai JSON export is not read yet: #424.)");
        return null;
    }

    /// <summary>
    /// A curated <c>.emodel.yaml</c>, which this command used to read and no longer does (issue
    /// #405). Refused with somewhere to go, rather than falling through to "unrecognized" — the
    /// file IS an event model and the person is not confused about that, so the useful answer says
    /// what changed and what to do.
    /// </summary>
    private static ImportedEventModel? refuseCurated(string path)
    {
        System.Console.Error.WriteLine(
            $"{path} is a curated event-model file, which is no longer an authoring format "
            + "(jasperfx#955). The event model is declared in code now: write an "
            + "EventModelDefinition over stub types and register it with "
            + "services.AddEventModel<TDefinition>(). What this command imports is the "
            + "eventmodelers.ai board export, and what it writes is that C#.");
        return null;
    }

    private static ImportedEventModel? importEmlang(ImportEventModelInput input, string yaml)
    {
        EmlangBoard board;
        try
        {
            board = EmlangReader.Read(yaml);
        }
        catch (EmlangFormatException e)
        {
            System.Console.Error.WriteLine($"Not readable as an emlang export: {e.Message}");
            return null;
        }

        var model = input.ModelFlag
                    ?? EmlangImport.PascalName(Path.GetFileName(input.FilePath).Split('.')[0]);
        var result = EmlangImport.ToCurated(board, model, input.NamespaceFlag);

        foreach (var line in result.Report) System.Console.WriteLine(line);

        var ns = input.NamespaceFlag
                 ?? CSharpModelWriter.Identifiers.Sanitize(model);
        var specs = input.SpecsFlag ? EmlangSpecWriter.Write(board, result.Model, ns) : null;
        var generated = CSharpModelWriter.Write(result.Model, ns, specs?.Additions);

        // --out names a DIRECTORY now, because the import writes two files. It may not exist yet:
        // letting File.WriteAllText throw dumped a raw Interop.ThrowExceptionForIoErrno stack
        // AFTER the segmentation report had printed and looked like success (issue #369), so
        // creating it is the friendlier reading of "write the generated code here".
        var outDirectory = input.OutFlag ?? Path.GetDirectoryName(Path.GetFullPath(input.FilePath))!;
        Directory.CreateDirectory(outDirectory);

        var stubsPath = Path.Combine(outDirectory, $"{model}Stubs.cs");
        var definitionPath = Path.Combine(outDirectory, $"{model}.cs");

        // Nothing is overwritten without --force (bobcat#423): these files are where the design
        // is corrected, so a second run must not throw the corrections away
        if (write(stubsPath, generated.Stubs, input.ForceFlag))
        {
            System.Console.WriteLine($"Wrote {generated.StubCount} stub record(s) to {stubsPath}.");
        }

        if (write(definitionPath, generated.Definition, input.ForceFlag))
        {
            System.Console.WriteLine(
                $"Wrote the event model to {definitionPath}. The segmentation above is a set of guesses, "
                + "so correct one with an edit rather than a re-import.");
        }

        if (specs is not null) writeSpecs(board, specs, Path.Combine(outDirectory, $"{model}Specs.cs"), input.ForceFlag);

        return result.Model;
    }

    /// <returns>Whether the file was written; an existing one is kept, and said so, unless <paramref name="force"/>.</returns>
    private static bool write(string path, string content, bool force)
    {
        if (File.Exists(path) && !force)
        {
            System.Console.WriteLine($"Kept {path}, which already exists (--force overwrites it).");
            return false;
        }

        File.WriteAllText(path, content, Encoding.UTF8);
        return true;
    }

    private static void writeSpecs(EmlangBoard board, GeneratedSpecs specs, string path, bool force)
    {
        foreach (var line in specs.Report) System.Console.WriteLine(line);

        if (write(path, specs.Code, force))
        {
            System.Console.WriteLine(
                $"Wrote {specs.Specs} specification(s) in {specs.Features} feature(s) to {path}. Start the "
                + "application's host in its AppFixture, and they run against it.");
            return;
        }

        // One-shot: a second run says what the model has that the specifications don't, and writes nothing
        var directory = Path.GetDirectoryName(path)!;
        var sources = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText);
        var missing = EmlangSpecWriter.MissingSpecs(board, sources);

        System.Console.WriteLine(missing.Count == 0
            ? "Every example in the model has a specification."
            : $"{missing.Count} example(s) in the model have no specification:");
        foreach (var name in missing) System.Console.WriteLine($"  {name}");
    }

    private static async Task<bool> pushAsync(string baseUrl, JasperFx.Events.EventModeling.EventModelDescriptor descriptor)
    {
        // The PUT goes to the full endpoint, not the console's base URL — the same trap
        // watch-event-model documents (base URL answers 404, endpoint answers 204).
        var url = $"{baseUrl.TrimEnd('/')}/api/event-model";
        var json = JsonSerializer.Serialize(descriptor, EventModelWire.Json);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var response = await client.PutAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            if (response.IsSuccessStatusCode)
            {
                System.Console.WriteLine($"Pushed to {url} — open {baseUrl.TrimEnd('/')}/event-model");
                return true;
            }

            System.Console.Error.WriteLine($"{url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return false;
        }
        catch (HttpRequestException e)
        {
            System.Console.Error.WriteLine($"Could not reach {url}: {e.Message}");
            return false;
        }
    }
}
