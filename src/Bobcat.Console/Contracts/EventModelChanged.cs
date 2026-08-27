using Wolverine;

namespace Bobcat.Console.Contracts;

/// <summary>
/// Issue #169 — the Event Model descriptor was replaced. Broadcast on the existing SignalR hub so
/// the Event Model page redraws without an F5.
/// </summary>
/// <remarks>
/// <para>
/// <c>PUT /api/event-model</c> stores the descriptor, but the page read <c>GET /api/event-model</c>
/// on load ONLY — so even a successful push needed a manual refresh. That was the last gap between
/// "I edited a handler" and "the diagram is right". Paired with Wolverine's <c>event-model --url</c>
/// (wolverine#4146), <c>dotnet watch run -- event-model --url http://localhost:5525</c> now redraws
/// the diagram on every save with no keystrokes at all.
/// </para>
/// <para>
/// It carries the model's NAME rather than the document. The descriptor is a whole-document replace
/// and can be large; the page already has a <c>GET</c> that serves it, and re-fetching keeps one
/// definition of how the page loads a model instead of two. The name is here so a console watching
/// more than one producer can say which model moved rather than just "something changed".
/// </para>
/// <para>
/// Deriving from <see cref="WebSocketMessage"/> does the same double duty it does for
/// <see cref="BatchedWebSocketPayload"/>: the one publish rule in <c>Program.cs</c> routes it to the
/// hub, and Wolverine's message naming makes the wire type <c>event_model_changed</c> with no
/// attribute. It deliberately does NOT ride the batch accumulator — that exists to coalesce a
/// firehose of monitor events, and a model push is a rare, single, latency-sensitive event.
/// </para>
/// </remarks>
public record EventModelChanged(string Name) : WebSocketMessage;
