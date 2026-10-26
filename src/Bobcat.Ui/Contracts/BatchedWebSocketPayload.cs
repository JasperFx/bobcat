using System.Text.Json;
using System.Text.Json.Serialization;
using Wolverine;

namespace Bobcat.Ui.Contracts;

/// <summary>
/// One message wrapped in the same {type, data} envelope shape the frontend's relayToStore
/// dispatcher already switches on, so unwrapping a batch is just re-dispatching each item. Type is
/// the message's snake_case Wolverine message name ("run_started", "runners_changed", ...) — for a
/// <see cref="MonitorEvent"/> the same string the STJ discriminator pins, see MonitorEvents.cs.
/// </summary>
/// <remarks>
/// <see cref="Data"/> is a <see cref="MonitorEvent"/> or one of the console's own notices
/// (<c>ConsoleNotices.cs</c>, bobcat#784). It is declared <c>object</c> so both can ride one batch,
/// and serialized by <see cref="BatchedDataConverter"/> AS its contract base type, because STJ emits
/// the polymorphic <c>type</c> discriminator only when it serializes through the type that declares
/// it: written as a plain object, a <c>RunStarted</c> loses its inner <c>"type":"run_started"</c>,
/// and an unwrapped item would stop being byte-compatible with a per-event envelope and with an
/// archived NDJSON line.
/// </remarks>
public record BatchedWebSocketItem(string Type, [property: JsonConverter(typeof(BatchedDataConverter))] object Data);

/// <summary>
/// Carries every monitor event flushed in one accumulator tick to the browser as a single
/// SignalR frame — CritterWatch's SignalRBatchAccumulator envelope, lifted (its frontend
/// unwrap case is the model for the one in relayToStore.ts). Deriving from WebSocketMessage
/// does double duty: the publish rule in Program.cs routes it to the hub, and Wolverine's
/// WebSocketMessage naming makes the wire type "batched_web_socket_payload" with no attribute.
/// Unlike CritterWatch there is no loop risk in that — the accumulator is fed directly by the
/// ingestion endpoint, not by a publish rule that this envelope could match.
/// </summary>
public record BatchedWebSocketPayload(BatchedWebSocketItem[] Items) : WebSocketMessage;

/// <summary>
/// Writes a batched item's data through its contract type — <see cref="MonitorEvent"/> for an event,
/// so its discriminator survives, and the runtime type for anything else. Reading is only ever needed
/// by a test, and yields the JSON as a <see cref="JsonElement"/>.
/// </summary>
public sealed class BatchedDataConverter : JsonConverter<object>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => JsonElement.ParseValue(ref reader);

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value is MonitorEvent ? typeof(MonitorEvent) : value.GetType(), options);
}
