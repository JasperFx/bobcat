using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Bobcat.EventModel.Emlang;

/// <summary>
/// bobcat#454. The stable codes an <see cref="ImportCallout"/> carries, for tooling such as Stoat
/// (JasperFx/stoat#91) to group the import's guesses and explain them. The message may be reworded;
/// a code is not, and codes are only ever added. Mirrors Wolverine's <c>ScaffoldNoticeCodes</c>.
/// </summary>
public static class ImportCalloutCodes
{
    /// <summary>The slice was inferred to start a stream: no example gives it an earlier event there.</summary>
    public const string InferredStreamStart = "inferred-stream-start";

    /// <summary>The slice was inferred to decide against an aggregate whose events its examples give first.</summary>
    public const string InferredAgainst = "inferred-against";

    /// <summary>The slice draws on several streams: several <c>[WriteModel]</c> streams, or a DCB decider.</summary>
    public const string SeveralStreams = "several-streams";

    /// <summary>
    /// The slice was inferred to decide against an aggregate, but the only evidence is events no
    /// imported slice emits, typically from a chapter that was not imported. It may really start the
    /// stream (CritterCrush's RequestHomeCheck).
    /// </summary>
    public const string StreamStartFromUnimportedChapter = "stream-start-from-unimported-chapter";

    /// <summary>The slice appends to a stream, and no example says whether it starts it, so it is assumed to append.</summary>
    public const string StreamStartUnknown = "stream-start-unknown";

    /// <summary>No aggregate could be declared or inferred for the command.</summary>
    public const string MissingAggregate = "missing-aggregate";

    // The spec project and the application it runs against (bobcat: import writes the spec project).
    // These are about the project, not a slice, so their Slice is empty and File names what they are about.

    /// <summary>A spec project file (.csproj, global.json, AssemblyAttributes.cs) already exists and was kept, never overwritten.</summary>
    public const string SpecProjectFileKept = "spec-project-file-kept";

    /// <summary>The existing spec project references nothing that provides a package the generated specifications need.</summary>
    public const string SpecProjectMissingReference = "spec-project-missing-reference";

    /// <summary>The existing spec project declares no <c>[assembly: EventModelName]</c>, so its slices cannot join the application's model.</summary>
    public const string SpecProjectMissingEventModelName = "spec-project-missing-event-model-name";

    /// <summary>The global.json that governs the spec project does not put <c>dotnet test</c> on Microsoft.Testing.Platform.</summary>
    public const string GlobalJsonMissingTestRunner = "global-json-missing-test-runner";

    /// <summary>The repository manages package versions centrally, so the versions the spec project needs go in Directory.Packages.props.</summary>
    public const string CentralPackageVersions = "central-package-versions";

    /// <summary>The spec project is not in the solution beside it.</summary>
    public const string SpecProjectNotInSolution = "spec-project-not-in-solution";

    /// <summary>No single application project was found, so the spec project has no ProjectReference to it.</summary>
    public const string AppProjectNotFound = "app-project-not-found";

    /// <summary>The application declares no public <c>Program</c>, which Alba needs to start it.</summary>
    public const string AppProgramNotPublic = "app-program-not-public";

    /// <summary>The application does not end with <c>RunJasperFxCommands(args)</c>, so <c>wolverine scaffold</c> and the codegen commands cannot run.</summary>
    public const string AppMissingRunJasperFxCommands = "app-missing-run-jasperfx-commands";

    /// <summary>The application references no WolverineFx.RuntimeCompilation, so a TypeLoadMode.Dynamic host fails to start.</summary>
    public const string AppMissingRuntimeCompilation = "app-missing-runtime-compilation";

    // The additive re-import (import-additive-merge): what a run without --force added to a file that
    // already existed, and what it could not place. These name the File they are about; a merged slice
    // or specification also names its Slice, and a merged or differing type its Type.

    /// <summary>A slice the board has and no definition declared was added to the end of an existing definition's <c>Configure</c>.</summary>
    public const string MergedSliceDeclaration = "merged-slice-declaration";

    /// <summary>An aggregate a merged slice names, and no definition declared, was declared beside it.</summary>
    public const string MergedAggregateDeclaration = "merged-aggregate-declaration";

    /// <summary>A stub type the board names and the project did not declare was added to an existing stub file.</summary>
    public const string MergedType = "merged-type";

    /// <summary>A specification for an example with none was added to the existing class bound to its feature.</summary>
    public const string MergedSpec = "merged-spec";

    /// <summary>A <c>global using</c> the specifications need was added to an existing GlobalUsings.cs.</summary>
    public const string MergedUsing = "merged-using";

    /// <summary>A type the project declares lacks a member the board names, or types it differently. The type was left alone.</summary>
    public const string TypeDiffersFromBoard = "type-differs-from-board";

    /// <summary>
    /// The board gives an existing slice roles no declaration of it says (an event it emits, a message it
    /// publishes, an event it is on, what it reads, produces or projects), so a statement reopening the slice by
    /// name, <c>model.Slice("X").Emits&lt;E&gt;()</c>, was added at the end of the definition's <c>Configure</c>.
    /// The slice's own declaration was left alone. <see cref="ImportCallout.Roles"/> lists what was added.
    /// </summary>
    public const string MergedSliceRoles = "merged-slice-roles";

    /// <summary>
    /// The board gives a slice a role no declaration of it says, and the re-import could not add it: the slice
    /// is declared in a file other than its chapter's definition. Nothing was changed.
    /// </summary>
    public const string SliceDiffersFromBoard = "slice-differs-from-board";

    /// <summary>
    /// The board's aggregate for an existing slice (what it decides against, which stream it starts) is not
    /// what its declaration says. Report only: the declaration was left alone.
    /// </summary>
    public const string SliceAggregateDiffers = "slice-aggregate-differs";

    /// <summary>The board's trigger for an existing slice (its label or kind) is not what its declaration says. Report only.</summary>
    public const string SliceTriggerDiffers = "slice-trigger-differs";

    /// <summary>The board's pattern for an existing slice (command, view, automation, translation) is not what its declaration says. Report only.</summary>
    public const string SlicePatternDiffers = "slice-pattern-differs";

    /// <summary>
    /// A declaration says a role the board no longer gives the slice, or never did: a role added by hand looks
    /// the same. Report only: nothing is ever removed.
    /// </summary>
    public const string SliceRoleRemovedOnBoard = "slice-role-removed-on-board";

    /// <summary>
    /// A definition declares a slice the board does not have, because the board removed it or because it was
    /// declared by hand. Report only: nothing is ever removed.
    /// </summary>
    public const string SliceRemovedOnBoard = "slice-removed-on-board";

    /// <summary>An existing file does not parse, so nothing was merged into it.</summary>
    public const string MergeSkippedUnparseable = "merge-skipped-unparseable";

    /// <summary>The namespace, definition class, <c>Configure</c> method or feature class the merge inserts into is not in the file.</summary>
    public const string MergeSkippedNoInsertionPoint = "merge-skipped-no-insertion-point";

    /// <summary>More than one place in the file could take the addition, so none was chosen.</summary>
    public const string MergeSkippedAmbiguous = "merge-skipped-ambiguous";
}

/// <summary>
/// bobcat#454. One guess the import made about a slice, or a gap it found, in a form a tool can read:
/// what it is about, a stable <see cref="Code"/>, the evidence it was drawn from, and the
/// <c>import-event-model</c> flag that settles it.
/// </summary>
/// <param name="Slice">The slice's name.</param>
/// <param name="Code">One of <see cref="ImportCalloutCodes"/>.</param>
/// <param name="Aggregates">The aggregates it is about, in the order the message names them.</param>
/// <param name="Message">The sentence the report and the generated definition say.</param>
/// <param name="Override">
/// The flag that states the answer outright and silences the callout (<c>--aggregate Slice=Type</c>,
/// <c>--starts-stream Slice=Type</c>); null when no flag settles it, such as a decision that needs a
/// DCB decider.
/// </param>
public sealed record ImportCallout(string Slice, string Code, IReadOnlyList<string> Aggregates, string Message, string? Override)
{
    /// <summary>The events the guess was drawn from.</summary>
    public IReadOnlyList<string> Events { get; init; } = [];

    /// <summary>The examples (tests) the guess was drawn from, by name.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];

    /// <summary>The chapters the evidence came from that were not imported, for <see cref="ImportCalloutCodes.StreamStartFromUnimportedChapter"/>.</summary>
    public IReadOnlyList<string> Chapters { get; init; } = [];

    /// <summary>The file a project-level callout is about (a .csproj, global.json or Program.cs); null for a slice's callout.</summary>
    public string? File { get; init; }

    /// <summary>The exact line, package or command that settles a project-level callout; null when the message says it all.</summary>
    public string? Fix { get; init; }

    /// <summary>The type a merge callout is about (<c>merged-type</c>, <c>type-differs-from-board</c>); null otherwise.</summary>
    public string? Type { get; init; }

    /// <summary>
    /// The slice roles a merge callout is about, each as <c>Verb(Name)</c>, e.g. <c>Emits(AppointmentNoShowRecorded)</c>:
    /// the roles added (<c>merged-slice-roles</c>), missing (<c>slice-differs-from-board</c>) or no longer on the
    /// board (<c>slice-role-removed-on-board</c>). Empty otherwise.
    /// </summary>
    public IReadOnlyList<string> Roles { get; init; } = [];
}

/// <summary>A file <c>import-event-model</c> wrote, or kept because it already exists.</summary>
/// <param name="Path">The full path.</param>
/// <param name="Mode">
/// <c>write</c>; <c>merge</c> when it existed and the re-import added to it; or <c>kept</c> when it existed,
/// <c>--force</c> was not given and there was nothing to add.
/// </param>
public sealed record ImportedFile(string Path, string Mode);

/// <summary>bobcat#454: the machine-readable form of an import, which <c>import-event-model --json</c> writes.</summary>
public static class ImportJson
{
    /// <summary>
    /// The import as JSON: the model, every callout with its code, evidence and override, the text
    /// report, and every file with how it was written. Written with <see cref="Utf8JsonWriter"/>, so no
    /// reflection.
    /// </summary>
    public static string Write(string model, IReadOnlyList<ImportCallout> callouts, IReadOnlyList<string> report,
        IReadOnlyList<ImportedFile>? files = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("model", model);

            json.WriteStartArray("callouts");
            foreach (var callout in callouts)
            {
                json.WriteStartObject();
                json.WriteString("slice", callout.Slice);
                json.WriteString("code", callout.Code);
                strings(json, "aggregates", callout.Aggregates);
                json.WriteString("message", callout.Message);

                json.WriteStartObject("evidence");
                strings(json, "events", callout.Events);
                strings(json, "examples", callout.Examples);
                if (callout.Chapters.Count > 0) strings(json, "chapters", callout.Chapters);
                json.WriteEndObject();

                if (callout.Override is not null) json.WriteString("override", callout.Override);
                if (callout.File is not null) json.WriteString("file", callout.File);
                if (callout.Fix is not null) json.WriteString("fix", callout.Fix);
                if (callout.Type is not null) json.WriteString("type", callout.Type);
                if (callout.Roles.Count > 0) strings(json, "roles", callout.Roles);
                json.WriteEndObject();
            }

            json.WriteEndArray();

            strings(json, "report", report);

            json.WriteStartArray("files");
            foreach (var file in files ?? [])
            {
                json.WriteStartObject();
                json.WriteString("path", file.Path);
                json.WriteString("mode", file.Mode);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void strings(Utf8JsonWriter json, string name, IEnumerable<string> values)
    {
        json.WriteStartArray(name);
        foreach (var value in values) json.WriteStringValue(value);
        json.WriteEndArray();
    }
}
