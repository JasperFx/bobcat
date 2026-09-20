using System.Text;
using System.Text.Json;
using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using JasperFx.CommandLine;

namespace Bobcat.Console;

public class ImportEventModelInput
{
    [Description("An event-model file: the curated format (schema/model/slices), or an eventmodelers.ai emlang board export")]
    public string FilePath { get; set; } = string.Empty;

    [Description("Model name for an emlang import; defaults to the file name. The curated format carries its own")]
    [FlagAlias("model", 'm')]
    public string? ModelFlag { get; set; }

    [Description("Root namespace recorded for synthesized type names on an emlang import")]
    public string? NamespaceFlag { get; set; }

    [Description("Where an emlang import writes the reviewable curated file; defaults beside the input")]
    [FlagAlias("out", 'o')]
    public string? OutFlag { get; set; }

    [Description("Base URL of a run console to push the assembled model to (e.g. http://localhost:5525)")]
    [FlagAlias("url", 'u')]
    public string? UrlFlag { get; set; }
}

/// <summary>
/// Issue #202 — <c>bobcat import-event-model &lt;file&gt;</c>: load a declared event model from a
/// file and optionally push it to a run console's viewer. That console lives in Stoat since the
/// 2026-09-18 fold; this side is unchanged, because it was only ever an HTTP client of it. An emlang board export goes through
/// segmentation first and lands as a curated file to review — the segmentation is a set of
/// reported guesses, and a wrong guess should be a one-line diff in that file, not a re-import.
/// Every decision lives in <c>Bobcat.EventModel</c> and is unit-tested; what remains here is
/// file and HTTP orchestration, kept deliberately thin (the <c>watch-event-model</c> precedent).
/// </summary>
[Description("Import a declared event-model file (curated YAML or an emlang board export) and optionally push it to a console", Name = "import-event-model")]
public class ImportEventModelCommand : JasperFxAsyncCommand<ImportEventModelInput>
{
    public ImportEventModelCommand()
    {
        Usage("Validate and summarize an event-model file").Arguments(x => x.FilePath);
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
            EventModelFileKind.Curated => readCurated(yaml),
            EventModelFileKind.Emlang => importEmlang(input, yaml),
            _ => describeUnrecognized(input.FilePath),
        };

        if (curated is null) return false;

        var descriptor = CuratedModelMapper.ToDescriptor(curated);
        System.Console.WriteLine(
            $"Model '{descriptor.Name}': {descriptor.Slices.Count} slice(s), "
            + $"{descriptor.Slices.Sum(x => x.Specifications.Count)} bound specification(s).");

        return input.UrlFlag is null || await pushAsync(input.UrlFlag, descriptor);
    }

    /// <summary>
    /// The file exists and is neither shape. Issue #369: this arm used to return null with no
    /// message at all, so the command exited 1 having printed nothing — where a missing file and
    /// an invalid curated file both reported properly. Naming the two shapes it tried is the
    /// whole fix.
    /// </summary>
    private static CuratedModelFile? describeUnrecognized(string path)
    {
        System.Console.Error.WriteLine(
            $"{path} is not an event-model file. Expected either the curated format (a `schema:`, "
            + "`model:` and `slices:` document) or an eventmodelers.ai emlang board export (a "
            + "`slices:` map of chapters with `steps:`).");
        return null;
    }

    private static CuratedModelFile? readCurated(string yaml)
    {
        var reading = CuratedModelReader.Read(yaml);

        // Warnings print whether or not the file validated (issue #318). Printing them only on
        // failure would hide every one of them, since a file carrying nothing but warnings
        // validates — which is exactly the silence the warning exists to break.
        foreach (var warning in reading.Warnings) System.Console.WriteLine($"⚠ {warning}");

        if (reading.Succeeded) return reading.File;

        System.Console.Error.WriteLine("The curated file did not validate:");
        foreach (var problem in reading.Problems) System.Console.Error.WriteLine($"  - {problem}");
        return null;
    }

    private static CuratedModelFile? importEmlang(ImportEventModelInput input, string yaml)
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

        var outPath = input.OutFlag
                      ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input.FilePath))!, $"{model}.emodel.yaml");

        // --out names a file, and its directory may not exist yet. Letting File.WriteAllText throw
        // dumped a raw Interop.ThrowExceptionForIoErrno stack AFTER the segmentation report had
        // already printed and looked like success (issue #369). Creating it is the friendlier
        // reading of "write the reviewable file here".
        var outDirectory = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(outDirectory)) Directory.CreateDirectory(outDirectory);

        File.WriteAllText(outPath, CuratedModelWriter.Write(result.Model), Encoding.UTF8);
        System.Console.WriteLine($"Curated model written to {outPath} — review the segmentation there before building against it.");

        return result.Model;
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
