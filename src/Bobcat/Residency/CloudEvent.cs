using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bobcat.Residency;

/// <summary>
/// A CloudEvent in structured mode — the envelope every message between a resident runner and a
/// monitor travels in (issue #390, settled by stoat#84).
/// </summary>
/// <remarks>
/// <para>
/// <b>Written and read by hand, deliberately.</b> Bobcat takes no CloudEvents package: the format
/// is six scalar attributes and a payload, the wire shape rather than an assembly is the contract
/// — exactly as it is for the monitor events in <c>Bobcat.Monitoring</c> — and a dependency here
/// would be one more version for a consuming project to fight over for no benefit. The shapes are
/// pinned by contract tests instead, which is the same bargain the two copies of
/// <c>MonitorEvents</c> already live under.
/// </para>
/// <para>
/// <b>Ids are GUIDs.</b> Not a style preference: the receiving side's mapper replaces any id it
/// cannot read as one with a freshly minted one, which would silently break a correlation the
/// sender was relying on. Correlation therefore also lives in <c>data</c> (<c>commandId</c>) and
/// never in the event id alone — an id is for de-duplication and for SSE resumption, not for
/// saying which command a message is about.
/// </para>
/// </remarks>
public sealed record CloudEvent
{
    /// <summary>The CloudEvents spec version this envelope is written to.</summary>
    public const string Version = "1.0";

    /// <summary>The media type a structured-mode CloudEvent is posted as.</summary>
    public const string MediaType = "application/cloudevents+json";

    private static readonly JsonSerializerOptions _json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [JsonPropertyName("specversion")]
    public string SpecVersion { get; init; } = Version;

    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    /// <summary>
    /// Who sent it: <c>bobcat/runner/{runnerId}</c> from a runner, <c>stoat</c> from the monitor.
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("datacontenttype")]
    public string? DataContentType { get; init; } = "application/json";

    [JsonPropertyName("time")]
    public DateTimeOffset? Time { get; init; }

    /// <summary>The payload, left as JSON until a reader knows which shape <see cref="Type"/> implies.</summary>
    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }

    /// <summary>The <c>source</c> a runner sends as.</summary>
    public static string SourceFor(string runnerId) => $"bobcat/runner/{runnerId}";

    /// <summary>A fresh event carrying <paramref name="data"/>, with a GUID id and the current time.</summary>
    public static CloudEvent From<T>(string source, string type, T data)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            Source = source,
            Type = type,
            Time = DateTimeOffset.UtcNow,
            Data = JsonSerializer.SerializeToElement(data, RunnerWire.Json)
        };

    public string ToJson() => JsonSerializer.Serialize(this, _json);

    /// <summary>
    /// The event, or null when the text is not one. Null rather than an exception for the same
    /// reason <see cref="DataAs{T}"/> is: what arrives on this stream comes from a monitor the
    /// runner has no control over, and a proxy's HTML error page is a thing to ignore rather than
    /// a thing to die of.
    /// </summary>
    public static CloudEvent? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CloudEvent>(json, _json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The payload as the shape this event's <see cref="Type"/> implies, or null when there is no
    /// payload or it does not read as one.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception, because a runner reads events a monitor sent and must treat
    /// anything it cannot understand as something to ignore rather than something to die of. A
    /// monitor it has no control over is not allowed to matter — see
    /// <see cref="ResidentRunner"/>'s invariant.
    /// </remarks>
    public T? DataAs<T>() where T : class
    {
        if (Data is not { } data) return null;

        try
        {
            return data.Deserialize<T>(RunnerWire.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
