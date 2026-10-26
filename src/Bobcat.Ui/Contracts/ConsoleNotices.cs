using Bobcat.Ui.Builds;
using Bobcat.Ui.Runners;
using Wolverine;

namespace Bobcat.Ui.Contracts;

// The console's own notices (bobcat#784): facts the CONSOLE decided, pushed to the browser over the
// same SignalR hub the monitor events ride, so the page never polls for them. None of these is a
// MonitorEvent — no publisher sends them, they are never ingested and never archived — which is why
// they are not in MonitorEvents.cs and carry no [JsonDerivedType]. Like EventModelChanged, each
// derives from WebSocketMessage so the one publish rule in BobcatUiHost routes it to the hub and
// Wolverine names it in snake_case (`run_orphaned`, `runners_changed`, `build_status_changed`);
// relayToStore.ts switches on those names by hand, and TypeScriptContracts mirrors the shapes.
//
// They ride the SignalRBatchAccumulator (SignalRBatchAccumulator.Notify) rather than being published
// one frame each, and the accumulator keeps only the LATEST notice of each type per flush: every one
// of them is a snapshot, so two in one window say nothing the second does not.

/// <summary>
/// The console stopped hearing from a run's publisher (bobcat#784): nothing arrived for it in
/// <c>MonitorRunRegistry.OrphanAfter</c>, so the run is now <c>orphaned</c> — the same state a
/// rehydrated run with no terminal event gets, and the state that lets it be ejected. Any later
/// event for the run clears it. <see cref="LastEventAt"/> is when the console last heard from it.
/// </summary>
public record RunOrphaned(Guid RunId, DateTimeOffset LastEventAt) : WebSocketMessage;

/// <summary>
/// The attached runners changed (bobcat#784): one registered, connected, disconnected, reported a
/// status, took or refused a command, or a command's run started. Carries the whole list as
/// <c>GET /api/runners</c> serves it, so a page replaces what it has rather than fetching again.
/// </summary>
public record RunnersChanged(IReadOnlyList<RunnerView> Runners) : WebSocketMessage;

/// <summary>
/// A watched project's build was reported (bobcat#651, pushed since bobcat#784). Carries every
/// project's latest report as <c>GET /api/build-status</c> serves it.
/// </summary>
public record BuildStatusChanged(IReadOnlyList<BuildStatusReported> Builds) : WebSocketMessage;
