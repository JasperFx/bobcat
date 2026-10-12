using System.Text;
using System.Text.Json;
using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using JasperFx.CommandLine;

namespace Bobcat.Console;

public class ImportEventModelInput
{
    [Description("An emlang model (YAML), or an eventmodelers.ai export (a board backup or config.json)")]
    public string FilePath { get; set; } = string.Empty;

    [Description("Model name; defaults to the file name. It is the merge key, so it must match what the code-derived sources call the model")]
    [FlagAlias("model", 'm')]
    public string? ModelFlag { get; set; }

    [Description("Namespace for the generated stubs and definition; defaults to the model name")]
    public string? NamespaceFlag { get; set; }

    [Description("Directory the generated C# is written to, normally the application project: Features/{Chapter}/{Slice}.cs and the definition. Defaults beside the input")]
    [FlagAlias("out", 'o')]
    public string? OutFlag { get; set; }

    [Description("Also write a WolverineFx.Bobcat specification for every example (test) in the model (bobcat#423)")]
    public bool SpecsFlag { get; set; }

    [Description("Directory the specification files are written to; defaults to a sibling of --out named for the spec namespace, e.g. ../CritterCrush.Specs")]
    [FlagAlias("specs-out", true)]
    public string? SpecsOutFlag { get; set; }

    [Description("Root namespace of the specifications, which should be the spec project's name; defaults to '<namespace>.Specs'. Each chapter folder adds a segment")]
    [FlagAlias("specs-namespace", true)]
    public string? SpecsNamespaceFlag { get; set; }

    [Description("WolverineFx.Bobcat version for a spec project the import writes (one is written when the spec directory has no .csproj); defaults to the newest published. The csproj keeps it in the WolverineFxBobcatVersion property")]
    [FlagAlias("wolverine-version", true)]
    public string? WolverineVersionFlag { get; set; }

    [Description("Bobcat package version for a spec project the import writes; defaults to this tool's own version")]
    [FlagAlias("bobcat-version", true)]
    public string? BobcatVersionFlag { get; set; }

    [Description("The application's .csproj, which the spec project references and the checks on Program read; defaults to the one .csproj in --out")]
    [FlagAlias("app-project", true)]
    public string? AppProjectFlag { get; set; }

    [Description("The application's event store -- marten, polecat or fisher -- which decides how the generated test host runs its async daemon")]
    [FlagAlias("store", true)]
    public string? StoreFlag { get; set; }

    [Description("Write one EventModelDefinition for the whole model instead of one per chapter")]
    [FlagAlias("single-definition", true)]
    public bool SingleDefinitionFlag { get; set; }

    [Description("The aggregate a slice decides against, as Slice=Type, which wins over the one inferred from the examples (bobcat#444). Several at once: --aggregate A=Order B=Order B=Customer")]
    // A List, not an array: JasperFx's enumerable flag assigns the List<T> it builds, and a string[]
    // property refused it with "List`1 cannot be converted to String[]", so --aggregate never parsed
    [FlagAlias("aggregate", true)]
    public List<string> AggregateFlag { get; set; } = [];

    [Description("The aggregate whose stream a slice STARTS, as Slice=Type, which wins over the inference (bobcat#454). Its --aggregate overrides are then what it decides against besides")]
    [FlagAlias("starts-stream", true)]
    public List<string> StartsStreamFlag { get; set; } = [];

    [Description("Import only these board chapters, space-separated: --chapter A B. The rest of the board is still read, so an example whose only earlier event comes from a chapter left out is called out (bobcat#454)")]
    [FlagAlias("chapter", true)]
    public List<string> ChapterFlag { get; set; } = [];

    [Description("Write the import to stdout as JSON -- every callout with its stable code, evidence and the flag that settles it, the report, and every file -- for tooling such as Stoat, instead of the text report (bobcat#454)")]
    [FlagAlias("json", true)]
    public bool JsonFlag { get; set; }

    [Description("Overwrite files that already exist. Without it an existing file is merged into: the board's new slices, types and specifications are added and nothing already there is changed")]
    public bool ForceFlag { get; set; }

    [Description("Base URL of a run console to push the assembled model to (e.g. http://localhost:5525)")]
    [FlagAlias("url", 'u')]
    public string? UrlFlag { get; set; }
}

/// <summary>
/// <c>bobcat import-event-model &lt;file&gt;</c> — read an emlang model, segment it, and write the
/// design out as <b>C#</b>: stub records with the fields the model names, one file per slice in a
/// folder per chapter (bobcat#441), plus one <c>EventModelDefinition</c> (issues #202, #405, #422).
/// With <c>--specs</c>, also one WolverineFx.Bobcat specification per example in the model
/// (bobcat#423), as <c>TestSupport.cs</c> and one file per slice (bobcat#440). When the spec directory holds no project, the spec project too (<see cref="SpecProjectWriter"/>), and the checks on the application. Nothing is overwritten
/// without <c>--force</c>; a re-import merges into the existing files instead, adding the board's new slices, types and
/// specifications (<see cref="ImportFileWriter"/>). Optionally pushes the assembled model to a run
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

        // bobcat#454: with --json nothing but the JSON reaches stdout, so a tool can parse it whole
        var run = new ImportRun(input.JsonFlag ? TextWriter.Null : System.Console.Out);

        var imported = EventModelFileSniffer.Sniff(yaml) switch
        {
            EventModelFileKind.Emlang => importEmlang(input, yaml, run),
            EventModelFileKind.EventModelersBoard or EventModelFileKind.EventModelersConfig => importEmlang(input, yaml, run),
            EventModelFileKind.Curated => refuseCurated(input.FilePath),
            _ => describeUnrecognized(input.FilePath),
        };

        if (imported is null) return false;

        // The layout the stubs were written with, so the pushed model declares every type where the
        // code puts it (bobcat#383)
        var descriptor = ImportedModelMapper.ToDescriptor(imported.Model, imported.Layout);
        run.Out.WriteLine(
            $"Model '{descriptor.Name}': {descriptor.Slices.Count} slice(s), "
            + $"{descriptor.Slices.Sum(x => x.Specifications.Count)} bound specification(s).");

        var pushed = input.UrlFlag is null || await pushAsync(input.UrlFlag, descriptor, run.Out);

        if (input.JsonFlag)
        {
            System.Console.WriteLine(ImportJson.Write(imported.Model.Model, run.Callouts, run.Report, run.Files));
        }

        return pushed;
    }

    /// <summary>What one run reports: the text, or with --json the callouts, report and files the JSON carries.</summary>
    private sealed class ImportRun(TextWriter output)
    {
        public TextWriter Out { get; } = output;
        public IReadOnlyList<ImportCallout> Callouts { get; set; } = [];
        public List<string> Report { get; } = [];
        public List<ImportedFile> Files { get; set; } = [];
    }

    /// <summary>
    /// The file exists and is not a board export. Issue #369: this arm used to return null with no
    /// message at all, so the command exited 1 having printed nothing. Naming the shape it wanted
    /// is the whole fix.
    /// </summary>
    private static Imported? describeUnrecognized(string path)
    {
        System.Console.Error.WriteLine(
            $"{path} is not an emlang file. Expected a top-level `slices:` map, each slice a list of "
            + "steps or a map of `steps:` and `tests:`. (An eventmodelers.ai export is a board backup or a config.json.)");
        return null;
    }

    /// <summary>
    /// A curated <c>.emodel.yaml</c>, which this command used to read and no longer does (issue
    /// #405). Refused with somewhere to go, rather than falling through to "unrecognized" — the
    /// file IS an event model and the person is not confused about that, so the useful answer says
    /// what changed and what to do.
    /// </summary>
    private static Imported? refuseCurated(string path)
    {
        System.Console.Error.WriteLine(
            $"{path} is a curated event-model file, which is no longer an authoring format "
            + "(jasperfx#955). The event model is declared in code now: write an "
            + "EventModelDefinition over stub types and register it with "
            + "services.AddEventModel<TDefinition>(). What this command imports is the "
            + "eventmodelers.ai board export, and what it writes is that C#.");
        return null;
    }

    private static Imported? importEmlang(ImportEventModelInput input, string yaml, ImportRun run)
    {
        EmlangBoard board;
        try
        {
            board = EventModelersJsonReader.LooksLikeJson(yaml) ? EventModelersJsonReader.Read(yaml) : EmlangReader.Read(yaml);
        }
        catch (EmlangFormatException e)
        {
            System.Console.Error.WriteLine($"Not readable: {e.Message}");
            return null;
        }

        var model = input.ModelFlag
                    ?? EmlangImport.PascalName(Path.GetFileName(input.FilePath).Split('.')[0]);
        IReadOnlyList<AggregateOverride> overrides;
        try
        {
            overrides = input.AggregateFlag.Select(AggregateOverride.Parse)
                .Concat(input.StartsStreamFlag.Select(AggregateOverride.ParseStartsStream))
                .ToList();
        }
        catch (FormatException e)
        {
            System.Console.Error.WriteLine(e.Message);
            return null;
        }

        EmlangImportResult result;
        try
        {
            result = EmlangImport.ToCurated(board, model, input.NamespaceFlag, overrides, input.ChapterFlag);
        }
        catch (ArgumentException e) when (e.ParamName == "chapters")
        {
            System.Console.Error.WriteLine(e.Message.Replace($" (Parameter '{e.ParamName}')", ""));
            return null;
        }

        // A slice the board moved to a chapter left out is still on the board, so an import of some chapters
        // compares the definitions against the whole board's slice names (slice-removed-on-board)
        var boardSlices = input.ChapterFlag.Count == 0
            ? null
            : EmlangImport.ToCurated(board, model, input.NamespaceFlag).Model.Slices.Select(ImportMerge.SliceName).ToList();

        // The spec writer reads the chapters imported, never the ones left out
        board = result.Board ?? board;
        run.Callouts = result.Callouts;
        run.Report.AddRange(result.Report);

        foreach (var line in result.Report) run.Out.WriteLine(line);

        var ns = input.NamespaceFlag
                 ?? CSharpModelWriter.Identifiers.Sanitize(model);
        var specsNamespace = input.SpecsNamespaceFlag ?? ns + ".Specs";
        var specs = input.SpecsFlag ? EmlangSpecWriter.Write(board, result.Model, ns, specsNamespace, input.StoreFlag) : null;
        var generated = CSharpModelWriter.Write(result.Model, ns, specs?.Additions, perChapter: !input.SingleDefinitionFlag);

        // --out names a DIRECTORY, the application project's. It may not exist yet: letting
        // File.WriteAllText throw dumped a raw Interop.ThrowExceptionForIoErrno stack AFTER the
        // segmentation report had printed and looked like success (issue #369), so creating it is
        // the friendlier reading of "write the generated code here".
        var outDirectory = Path.GetFullPath(input.OutFlag ?? Path.GetDirectoryName(Path.GetFullPath(input.FilePath))!);
        Directory.CreateDirectory(outDirectory);

        // Nothing is overwritten without --force (bobcat#423): these files are where the design is
        // corrected. An existing file is merged into instead -- the board's new slices, types and
        // specifications are added, nothing already there is changed -- so a re-import is additive
        var files = new ImportFileWriter(input.ForceFlag);
        run.Files = files.Files;

        var stubs = files.WriteStubs(outDirectory, generated.StubFiles);
        run.Out.WriteLine(
            $"Wrote {stubs.Written} stub file(s) under {Path.Combine(outDirectory, ModelLayout.FeaturesFolder)}"
            + (stubs.Merged > 0 ? $"; merged new types into {stubs.Merged}" : "")
            + (stubs.Kept > 0 ? $"; kept {stubs.Kept} that already have every type the board names (--force overwrites them)." : "."));

        var definitionFiles = generated.DefinitionFiles.Count > 0
            ? generated.DefinitionFiles
            : [new GeneratedFile($"{model}.cs", generated.Definition)];

        // bobcat#448: one definition per chapter, beside its stubs, each joining the application's model
        var definitions = files.WriteDefinitions(outDirectory, definitionFiles, result.Model, boardSlices);
        run.Out.WriteLine(
            $"Wrote {definitions.Written} event model definition(s)"
            + (definitions.Merged > 0 ? $", merged new slices or roles into {definitions.Merged}" : "")
            + (definitions.Kept > 0 ? $", kept {definitions.Kept}" : "")
            + ": " + string.Join(", ", definitionFiles.Select(x => x.Path))
            + ". Register them with AddEventModel<T>() (or let Wolverine discover them). The segmentation above is a set of "
            + "guesses, so correct one with an edit rather than a re-import.");

        var specsDirectory = Path.GetFullPath(input.SpecsOutFlag
                                              ?? Path.Combine(Path.GetDirectoryName(outDirectory) ?? outDirectory, specsNamespace));
        if (specs is not null) writeSpecs(result.Model, specs, specsDirectory, files, run);

        reportMerge(files, run);

        if (specs is not null) writeSpecProject(input, model, specsNamespace, outDirectory, specsDirectory, run);

        return new Imported(result.Model, generated.Layout);
    }

    /// <summary>What an import produced: the model, and where its stubs put every type.</summary>
    private sealed record Imported(ImportedEventModel Model, ModelLayout Layout);

    /// <summary>
    /// What the additive re-import did: a line per file merged into, and every callout it raised. The
    /// merges are said plainly; what could not be placed, or differs from the board, is a ⚠ line.
    /// </summary>
    private static void reportMerge(ImportFileWriter files, ImportRun run)
    {
        foreach (var line in files.Lines)
        {
            run.Out.WriteLine(line);
            run.Report.Add(line);
        }

        foreach (var callout in files.Callouts.Where(x => !x.Code.StartsWith("merged-", StringComparison.Ordinal)))
        {
            run.Out.WriteLine($"⚠ [{callout.Code}] {callout.Message}");
            if (callout.Fix is not null) run.Out.WriteLine($"    {callout.Fix}");
        }

        run.Callouts = run.Callouts.Concat(files.Callouts).ToList();
    }

    private static void writeSpecs(ImportedEventModel model, GeneratedSpecs specs, string directory, ImportFileWriter files, ImportRun run)
    {
        run.Report.AddRange(specs.Report);
        foreach (var line in specs.Report) run.Out.WriteLine(line);

        Directory.CreateDirectory(directory);
        var counts = files.WriteSpecs(directory, specs.Files);
        run.Out.WriteLine(
            $"Wrote {counts.Written} specification file(s) to {directory} ({specs.Specs} specification(s) in {specs.Features} feature(s) in all). "
            + $"{EmlangSpecWriter.TestSupportFile} starts the application's own host through Alba.");
        if (counts.Kept + counts.Merged == 0) return;

        // What the model has that the specifications there still don't, after the merge: an example
        // whose specification could not be placed (each such file is a callout too)
        var sources = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText);
        var missing = EmlangSpecWriter.MissingSpecs(model, sources);

        if (counts.Merged > 0) run.Out.WriteLine($"Merged new specifications into {counts.Merged} file(s) that already exist.");
        if (counts.Kept > 0) run.Out.WriteLine($"Kept {counts.Kept} specification file(s) that already exist (--force overwrites them).");
        run.Out.WriteLine(missing.Count == 0
            ? "Every example in the model has a specification."
            : $"{missing.Count} example(s) in the model have no specification:");
        foreach (var name in missing) run.Out.WriteLine($"  {name}");
    }

    /// <summary>
    /// The spec project around the specifications, when the directory has none, and the checks on the
    /// application. Every decision is <see cref="SpecProjectWriter.Plan"/>'s; nothing it names that already
    /// exists is written, with or without --force.
    /// </summary>
    private static void writeSpecProject(ImportEventModelInput input, string model, string specsNamespace, string outDirectory,
        string specsDirectory, ImportRun run)
    {
        var appProject = input.AppProjectFlag is { Length: > 0 } flag
            ? Path.GetFullPath(flag)
            : SpecProjectWriter.FindAppProject(outDirectory);

        var plan = SpecProjectWriter.Plan(new SpecProjectRequest(specsDirectory, specsNamespace, model, appProject,
            input.BobcatVersionFlag, input.WolverineVersionFlag));

        foreach (var file in plan.Files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
            File.WriteAllText(file.Path, file.Content); // UTF-8 without a BOM
            run.Files.Add(new ImportedFile(file.Path, "write"));
        }

        foreach (var file in plan.Kept) run.Files.Add(new ImportedFile(file.Path, "kept"));

        run.Report.AddRange(plan.Report);
        foreach (var line in plan.Report) run.Out.WriteLine(line);

        foreach (var callout in plan.Callouts)
        {
            run.Out.WriteLine($"⚠ [{callout.Code}] {callout.Message}");
            if (callout.Fix is not null) run.Out.WriteLine($"    {callout.Fix.Replace("\n", "\n    ")}");
        }

        run.Callouts = run.Callouts.Concat(plan.Callouts).ToList();
    }

    private static async Task<bool> pushAsync(string baseUrl, JasperFx.Events.EventModeling.EventModelDescriptor descriptor, TextWriter output)
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
                output.WriteLine($"Pushed to {url} — open {baseUrl.TrimEnd('/')}/event-model");
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
