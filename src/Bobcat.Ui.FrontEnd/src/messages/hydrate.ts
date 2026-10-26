import { relayToStore } from './relayToStore'
import { useRunnersStore } from '@/stores/runners-store'
import { useRunsStore, type RunSummary } from '@/stores/runs-store'

/**
 * Seed the SPA on page load and on every SignalR reconnect, so a fresh tab shows current state
 * immediately instead of waiting for the next live event. Best-effort throughout.
 *
 * Three requests, however many runs the board carries (bobcat#784): the board summary, the
 * runners, the build reports. A run's own events are loaded only for a run that is live — its
 * card's progress needs them, and the live stream carries only what happens from now on — and for
 * a run whose events were already loaded, since a reconnect may have missed some. Everything else
 * waits for its page to open (`loadRunEvents`). This replaced a replay of every run's archive, one
 * request after another: 824 runs on a shared box was 824 fetches and 71 MB before the counts
 * stopped climbing.
 */
export async function hydrateFromServer(fetchImpl: typeof fetch = fetch): Promise<void> {
  await Promise.all([hydrateBoard(fetchImpl), useRunnersStore().load(fetchImpl)])
}

/** One request for the board, then the archives of the runs that need them, side by side. */
async function hydrateBoard(fetchImpl: typeof fetch): Promise<void> {
  const store = useRunsStore()
  let summaries: RunSummary[]
  // A failed summary is as loaded as the board will get (bobcat#798): it must not leave the board
  // waiting on a summary that is not coming.
  try {
    const response = await fetchImpl('/api/runs')
    if (!response.ok) {
      store.markBoardLoaded()
      return
    }
    summaries = (await response.json()) as RunSummary[]
  } catch {
    store.markBoardLoaded()
    return // Hydration is best-effort; the live stream still works without it.
  }

  store.pruneTo(summaries.map((s) => s.runId))
  store.applySummaries(summaries)
  store.markBoardLoaded()

  const wanted = summaries.filter((s) => (!s.finished && !s.orphaned) || store.runById(s.runId)?.eventsLoaded)
  await Promise.all(wanted.map((s) => loadRunEvents(s.runId, fetchImpl)))
}

// One archive load in flight per run: a run page and the board asking at once share it.
const inFlight = new Map<string, Promise<void>>()

/**
 * Replay one run's archive into the store — when its page opens, or because it is live
 * (bobcat#784). The server's NDJSON is the same event stream the live SignalR path delivers, so
 * the replay reuses the store's own fold via relayToStore: one reduction, two transports, nothing
 * to keep in sync. Replay over already-arrived live events is safe because the store's handlers
 * upsert (see the stepNumber guard in handleStepStarted). A run the summary called orphaned is
 * marked so again afterwards: every replayed event reads as the publisher speaking, and it is not.
 */
export function loadRunEvents(runId: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  const pending = inFlight.get(runId)
  if (pending) return pending

  const load = (async () => {
    const store = useRunsStore()
    const orphaned = store.runById(runId)?.orphaned ?? false
    try {
      const response = await fetchImpl(`/api/runs/${runId}/export?format=ndjson`)
      if (!response.ok) return

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

      if (orphaned) store.markOrphaned(runId)
      store.markEventsLoaded(runId)
    } catch {
      // The page shows what it has; the next open asks again.
    } finally {
      inFlight.delete(runId)
    }
  })()

  inFlight.set(runId, load)
  return load
}

/**
 * Load a run's events if the store does not have them (bobcat#784) — what a run page and a spec
 * page do on opening. Resolves once the store has them, or at once when it already does.
 */
export function ensureRunEvents(runId: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  const run = useRunsStore().runById(runId)
  return run?.eventsLoaded ? Promise.resolve() : loadRunEvents(runId, fetchImpl)
}
