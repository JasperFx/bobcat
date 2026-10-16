import { useEventModelStore } from '@/stores/event-model-store'
import { useRunsStore } from '@/stores/runs-store'
import type { BatchedWebSocketPayload, MonitorEnvelope } from './monitor-events'
import { routeMonitorEvent } from './relayRoutes'

/**
 * Fan incoming SignalR envelopes out to the Pinia stores — the CritterWatch dispatcher
 * pattern: one switch on the snake_case message type name, stores never touch SignalR
 * themselves.
 *
 * Hand-written. Every MonitorEvent is routed by `routeMonitorEvent`, which is GENERATED from the
 * C# records into relayRoutes.ts (gitignored, bobcat#604), so a new event needs no edit here —
 * only a `handle{Type}` on the runs store, which vue-tsc insists on. The cases below are the
 * envelopes that are not MonitorEvents, so no generator knows about them.
 */
export function relayToStore(message: unknown): void {
  const envelope = (typeof message === 'string' ? JSON.parse(message) : message) as MonitorEnvelope
  if (!envelope || typeof envelope.type !== 'string') return

  switch (envelope.type) {
    case 'batched_web_socket_payload': {
      // Server-side batched frame: unwrap and dispatch each inner {type, data} item in
      // order — every item is exactly the per-event envelope handled here.
      const batch = envelope.data as BatchedWebSocketPayload
      for (const item of batch?.items ?? []) {
        relayToStore(item)
      }
      return
    }
    // Issue #169: EventModelChanged is not a MonitorEvent. The push carries the model's NAME, not
    // the document — a whole-document replace can be large, and the page already has a GET that
    // serves it. Re-fetching keeps one definition of how a model is loaded instead of two that can
    // disagree.
    case 'event_model_changed':
      void useEventModelStore().refresh()
      return
  }

  // Unknown message types fall through routeMonitorEvent unrouted: forward compatibility, not errors.
  routeMonitorEvent(useRunsStore(), envelope)
}
