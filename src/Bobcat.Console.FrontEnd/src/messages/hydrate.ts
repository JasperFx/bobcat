import { relayToStore } from './relayToStore'
import { useRunsStore } from '@/stores/runs-store'

interface RunSummaryDto {
  runId: string
  orphaned: boolean
}

/**
 * Seed the runs store from the server's registry, so a page load (or reconnect) shows current
 * state immediately instead of waiting for the next live event.
 *
 * The mechanism is deliberately a REPLAY, not a snapshot DTO: the server's NDJSON archive is
 * the same event stream the live SignalR path delivers, so hydration reuses the store's own
 * fold via relayToStore — one reduction, two transports, nothing to keep in sync. Replay over
 * already-arrived live events is safe because the store's handlers upsert (see the stepId
 * guard in handleStepStarted).
 */
export async function hydrateFromServer(fetchImpl: typeof fetch = fetch): Promise<void> {
  let summaries: RunSummaryDto[]
  try {
    const response = await fetchImpl('/api/runs')
    if (!response.ok) return
    summaries = (await response.json()) as RunSummaryDto[]
  } catch {
    return // Hydration is best-effort; the live stream still works without it.
  }

  const store = useRunsStore()
  store.pruneTo(summaries.map((s) => s.runId))

  for (const summary of summaries) {
    try {
      const response = await fetchImpl(`/api/runs/${summary.runId}/export?format=ndjson`)
      if (!response.ok) continue

      const ndjson = await response.text()
      for (const line of ndjson.split('\n')) {
        const trimmed = line.trim()
        if (!trimmed) continue
        try {
          // Archived lines are flat events with an inline `type` — wrap into the
          // {type, data} envelope shape the dispatcher expects.
          const event = JSON.parse(trimmed) as { type: string }
          relayToStore({ type: event.type, data: event })
        } catch {
          // A torn line must not sink the rest of the replay.
        }
      }

      if (summary.orphaned) store.markOrphaned(summary.runId)
    } catch {
      // Skip this run, keep hydrating the others.
    }
  }
}
