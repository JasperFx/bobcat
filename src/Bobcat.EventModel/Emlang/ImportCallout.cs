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
}

/// <summary>A file <c>import-event-model</c> wrote, or kept because it already exists.</summary>
/// <param name="Path">The full path.</param>
/// <param name="Mode"><c>write</c>, or <c>kept</c> when it existed and <c>--force</c> was not given.</param>
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
