import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { ensureRunEvents, hydrateFromServer, loadRunEvents } from '../hydrate'
import { useRunnersStore } from '@/stores/runners-store'
import { useRunsStore, type RunSummary } from '@/stores/runs-store'

/**
 * bobcat#784 — the board hydrates from ONE request, whatever the run count, and a run's events are
 * loaded only when something wants them: its page, or its card while it is live. Before this,
 * hydration fetched every run's archive one after another on every load and every reconnect — 824
 * runs on a shared box was 824 requests and 71 MB, with the counts visibly climbing for a long time.
 */
const RUN = '9a1f1a1e-0000-0000-0000-0000000000aa'
const ORPHAN = '9a1f1a1e-0000-0000-0000-0000000000bb'
const LIVE = '9a1f1a1e-0000-0000-0000-0000000000cc'

/** NDJSON exactly as the registry archives it: flat events with an inline `type`. */
const finishedRunNdjson = [
  `{"type":"run_started","runId":"${RUN}","suite":"Hydrated","repository":"/repo","branch":"main","mode":"in-process","startedAt":"2026-07-31T10:00:00Z","totalScenarios":1,"tag":"build-42"}`,
  `{"type":"scenario_started","runId":"${RUN}","uid":"F/s","feature":"F","scenario":"s","attempt":1,"at":"2026-07-31T10:00:01Z"}`,
  `{"type":"step_started","runId":"${RUN}","uid":"F/s","stepId":"s1","kind":"Given","text":"a thing"}`,
  `{"type":"step_finished","runId":"${RUN}","uid":"F/s","stepId":"s1","status":"success","durationMs":4,"errorMessage":null}`,
  `{"type":"scenario_finished","runId":"${RUN}","uid":"F/s","outcome":"CleanPass","attempts":1,"durationMs":9,"errorMessage":null}`,
  `{"type":"run_finished","runId":"${RUN}","exitCode":0,"passed":1,"failed":0,"passedOnRetry":0,"indeterminate":0,"finishedAt":"2026-07-31T10:00:02Z"}`,
].join('\n')

const liveRunNdjson = [
  `{"type":"run_started","runId":"${LIVE}","suite":"Going","repository":"/repo","branch":null,"mode":"in-process","startedAt":"2026-07-31T11:00:00Z","totalScenarios":2}`,
  `{"type":"scenario_started","runId":"${LIVE}","uid":"F/a","feature":"F","scenario":"a","attempt":1,"at":"2026-07-31T11:00:01Z"}`,
  `{"type":"scenario_finished","runId":"${LIVE}","uid":"F/a","outcome":"CleanPass","attempts":1,"durationMs":9,"errorMessage":null}`,
].join('\n')

function summary(overrides: Partial<RunSummary> & { runId: string }): RunSummary {
  return {
    suite: 'Suite',
    repository: '/repo',
    repositorySlug: 'JasperFx/repo',
    branch: 'main',
    mode: 'in-process',
    finished: false,
    orphaned: false,
    exitCode: null,
    scenarios: 0,
    startedAt: '2026-07-31T10:00:00Z',
    finishedAt: null,
    ...overrides,
  }
}

const finishedSummary = summary({
  runId: RUN, suite: 'Hydrated', finished: true, exitCode: 0, scenarios: 1, scenariosFinished: 1, totalScenarios: 1,
  finishedAt: '2026-07-31T10:00:02Z', passed: 1, failed: 0, passedOnRetry: 0, indeterminate: 0, skipped: null,
  tag: 'build-42', session: 's-abc', command: null, lastEventAt: '2026-07-31T10:00:02Z',
})
const orphanSummary = summary({ runId: ORPHAN, suite: 'Lost', orphaned: true, totalScenarios: 3, startedAt: '2026-07-31T09:00:00Z' })
const liveSummary = summary({ runId: LIVE, suite: 'Going', branch: null, totalScenarios: 2, startedAt: '2026-07-31T11:00:00Z' })

/** A server whose every request is counted, so a test can say how many the board cost. */
function fakeServer(routes: Record<string, { ok: boolean; body: string }>) {
  const requests: string[] = []
  // `unknown` rather than RequestInfo: the vitest tsconfig strips DOM lib types.
  const fetchImpl = (async (input: unknown) => {
    const url = String(input)
    requests.push(url)
    const route = routes[url]
    if (!route) return { ok: false, json: async () => null, text: async () => '' } as Response
    return {
      ok: route.ok,
      json: async () => JSON.parse(route.body),
      text: async () => route.body,
    } as Response
  }) as typeof fetch
  return { fetchImpl, requests }
}

const board = (...summaries: RunSummary[]) => ({ '/api/runs': { ok: true, body: JSON.stringify(summaries) } })
const archive = (runId: string, ndjson: string) => ({ [`/api/runs/${runId}/export?format=ndjson`]: { ok: true, body: ndjson } })

describe('hydrateFromServer', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('seeds the board from one request however many runs there are, loading only the live run’s events', async () => {
    const many = Array.from({ length: 40 }, (_, i) => summary({ runId: `9a1f1a1e-0000-0000-0000-${String(i).padStart(12, '0')}`, finished: true, passed: 1, failed: 0 }))
    const server = fakeServer({
      ...board(finishedSummary, orphanSummary, liveSummary, ...many),
      ...archive(RUN, finishedRunNdjson),
      ...archive(LIVE, liveRunNdjson),
    })

    await hydrateFromServer(server.fetchImpl)

    expect(server.requests.filter((u) => u === '/api/runs')).toHaveLength(1)
    expect(server.requests.filter((u) => u.includes('/export'))).toEqual([`/api/runs/${LIVE}/export?format=ndjson`])
    expect(server.requests).toContain('/api/runners')
    expect(server.requests).toContain('/api/build-status')

    const store = useRunsStore()
    expect(store.allRuns).toHaveLength(43)

    // A finished run is its card: everything the summary says, and none of its events yet.
    const finished = store.runById(RUN)!
    expect(finished.suite).toBe('Hydrated')
    expect(finished.finished).toBe(true)
    expect(finished.counts).toEqual({ passed: 1, failed: 0, passedOnRetry: 0, indeterminate: 0, skipped: null })
    expect(finished.tag).toBe('build-42')
    expect(finished.session).toBe('s-abc')
    expect(finished.lastEventAt).toBe('2026-07-31T10:00:02Z')
    expect(finished.eventsLoaded).toBe(false)
    expect(Object.keys(finished.scenarios)).toHaveLength(0)
    expect(store.repositoryOf(finished)).toBe('JasperFx/repo')

    const orphan = store.runById(ORPHAN)!
    expect(orphan.suite).toBe('Lost')
    expect(orphan.orphaned).toBe(true)
    expect(orphan.finished).toBe(false)

    // The live run's card needs its progress, so its archive was replayed.
    const live = store.runById(LIVE)!
    expect(live.eventsLoaded).toBe(true)
    expect(live.scenarios['F/a']?.status).toBe('passed')
    expect(store.progressOf(live)).toBe(0.5)
  })

  it('loads a run’s events when its page asks, and then keeps them current on a reconnect', async () => {
    const server = fakeServer({ ...board(finishedSummary), ...archive(RUN, finishedRunNdjson) })
    await hydrateFromServer(server.fetchImpl)
    const store = useRunsStore()
    expect(store.runById(RUN)!.eventsLoaded).toBe(false)

    await ensureRunEvents(RUN, server.fetchImpl)
    await ensureRunEvents(RUN, server.fetchImpl)

    const run = store.runById(RUN)!
    expect(run.eventsLoaded).toBe(true)
    expect(run.scenarios['F/s']?.steps).toHaveLength(1)
    expect(server.requests.filter((u) => u.includes('/export'))).toHaveLength(1)

    // A reconnect refreshes the summary, and replays only the archives already loaded.
    await hydrateFromServer(server.fetchImpl)
    expect(server.requests.filter((u) => u === '/api/runs')).toHaveLength(2)
    expect(server.requests.filter((u) => u.includes('/export'))).toHaveLength(2)
  })

  it('replaying over already-arrived live events does not duplicate steps', async () => {
    const store = useRunsStore()
    // Live events landed before hydration finished.
    store.handleScenarioStarted({
      runId: RUN, uid: 'F/s', feature: 'F', scenario: 's', attempt: 1, at: '2026-07-31T10:00:01Z',
    })
    store.handleStepStarted({ runId: RUN, uid: 'F/s', stepId: 's1', kind: 'Given', text: 'a thing' })

    await loadRunEvents(RUN, fakeServer(archive(RUN, finishedRunNdjson)).fetchImpl)

    const scenario = store.runById(RUN)!.scenarios['F/s']!
    expect(scenario.steps).toHaveLength(1)
    expect(scenario.steps[0]!.status).toBe('passed')
  })

  it('an orphaned run replayed for its page stays orphaned, since a replayed event is not the publisher speaking', async () => {
    const orphanNdjson = `{"type":"run_started","runId":"${ORPHAN}","suite":"Lost","repository":"/repo","branch":null,"mode":"in-process","startedAt":"2026-07-31T09:00:00Z","totalScenarios":3}`
    const server = fakeServer({ ...board(orphanSummary), ...archive(ORPHAN, orphanNdjson) })
    await hydrateFromServer(server.fetchImpl)

    await ensureRunEvents(ORPHAN, server.fetchImpl)

    const orphan = useRunsStore().runById(ORPHAN)!
    expect(orphan.orphaned).toBe(true)
    expect(orphan.eventsLoaded).toBe(true)
  })

  it('prunes local runs the server no longer knows', async () => {
    const store = useRunsStore()
    store.handleRunHeartbeat({ runId: 'dead-local-run', at: '2026-07-31T10:00:00Z' })

    await hydrateFromServer(fakeServer(board()).fetchImpl)

    expect(store.allRuns).toHaveLength(0)
  })

  it('is best-effort: a failing server leaves the store untouched', async () => {
    const store = useRunsStore()
    store.handleRunHeartbeat({ runId: RUN, at: '2026-07-31T10:00:00Z' })

    await hydrateFromServer(fakeServer({}).fetchImpl)
    await hydrateFromServer((async () => {
      throw new Error('down')
    }) as unknown as typeof fetch)

    expect(store.runById(RUN)).toBeDefined()
    expect(useRunnersStore().runners).toEqual([])
  })

  it('marks the board loaded once its summary is applied, and when the summary fails, so it never waits forever (bobcat#798)', async () => {
    expect(useRunsStore().boardLoaded).toBe(false)
    await hydrateFromServer(fakeServer(board(finishedSummary)).fetchImpl)
    expect(useRunsStore().boardLoaded).toBe(true)

    setActivePinia(createPinia())
    await hydrateFromServer(fakeServer({}).fetchImpl) // /api/runs answers not-ok
    expect(useRunsStore().boardLoaded).toBe(true)

    setActivePinia(createPinia())
    await hydrateFromServer((async () => {
      throw new Error('down')
    }) as unknown as typeof fetch)
    expect(useRunsStore().boardLoaded).toBe(true)
  })

  it('a torn archive line does not sink the rest of the replay', async () => {
    await loadRunEvents(RUN, fakeServer(archive(RUN, '{"type":"run_hea\n' + finishedRunNdjson)).fetchImpl)

    expect(useRunsStore().runById(RUN)?.finished).toBe(true)
  })
})
