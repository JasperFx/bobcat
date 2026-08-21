using Wolverine;

namespace Bobcat.Console.Contracts;

/// <summary>
/// One monitor event wrapped in the same {type, data} envelope shape the frontend's
/// relayToStore dispatcher already switches on, so unwrapping a batch is just re-dispatching
/// each item. Type is the event's snake_case Wolverine message name ("run_started", ...) —
/// the same string the STJ discriminator pins, see MonitorEvents.cs.
/// </summary>
public record BatchedWebSocketItem(string Type, MonitorEvent Data);

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
