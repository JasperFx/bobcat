import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { hydrateFromServer } from '../hydrate'
import { useRunsStore } from '@/stores/runs-store'

const RUN = '9a1f1a1e-0000-0000-0000-0000000000aa'
const ORPHAN = '9a1f1a1e-0000-0000-0000-0000000000bb'

/** NDJSON exactly as the registry archives it: flat events with an inline `type`. */
const finishedRunNdjson = [
  `{"type":"run_started","runId":"${RUN}","suite":"Hydrated","repository":"/repo","branch":"main","mode":"in-process","startedAt":"2026-07-31T10:00:00Z","totalScenarios":1}`,
  `{"type":"scenario_started","runId":"${RUN}","uid":"F/s","feature":"F","scenario":"s","attempt":1,"at":"2026-07-31T10:00:01Z"}`,
  `{"type":"step_started","runId":"${RUN}","uid":"F/s","stepId":"s1","kind":"Given","text":"a thing"}`,
  `{"type":"step_finished","runId":"${RUN}","uid":"F/s","stepId":"s1","status":"success","durationMs":4,"errorMessage":null}`,
  `{"type":"scenario_finished","runId":"${RUN}","uid":"F/s","outcome":"CleanPass","attempts":1,"durationMs":9,"errorMessage":null}`,
  `{"type":"run_finished","runId":"${RUN}","exitCode":0,"passed":1,"failed":0,"passedOnRetry":0,"indeterminate":0,"finishedAt":"2026-07-31T10:00:02Z"}`,
].join('\n')

const orphanNdjson = `{"type":"run_started","runId":"${ORPHAN}","suite":"Lost","repository":"/repo","branch":null,"mode":"in-process","startedAt":"2026-07-31T09:00:00Z","totalScenarios":3}`

function fakeFetch(routes: Record<string, { ok: boolean; body: string }>): typeof fetch {
  // `unknown` rather than RequestInfo: the vitest tsconfig strips DOM lib types.
  return (async (input: unknown) => {
    const url = String(input)
    const route = routes[url]
    if (!route) return { ok: false, json: async () => null, text: async () => '' } as Response
    return {
      ok: route.ok,
      json: async () => JSON.parse(route.body),
      text: async () => route.body,
    } as Response
  }) as typeof fetch
}

describe('hydrateFromServer', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('replays archived runs into the store, including orphan marking', async () => {
    await hydrateFromServer(
      fakeFetch({
        '/api/runs': {
          ok: true,
          body: JSON.stringify([
            { runId: RUN, orphaned: false },
            { runId: ORPHAN, orphaned: true },
          ]),
        },
        [`/api/runs/${RUN}/export?format=ndjson`]: { ok: true, body: finishedRunNdjson },
        [`/api/runs/${ORPHAN}/export?format=ndjson`]: { ok: true, body: orphanNdjson },
      }),
    )

    const store = useRunsStore()
    const run = store.runById(RUN)!
    expect(run.suite).toBe('Hydrated')
    expect(run.finished).toBe(true)
    expect(run.counts?.passed).toBe(1)
    expect(run.scenarios['F/s']?.steps).toHaveLength(1)

    const orphan = store.runById(ORPHAN)!
    expect(orphan.suite).toBe('Lost')
    expect(orphan.orphaned).toBe(true)
    expect(orphan.finished).toBe(false)
  })

  it('replaying over already-arrived live events does not duplicate steps', async () => {
    const store = useRunsStore()
    // Live events landed before hydration finished.
    store.handleScenarioStarted({
      runId: RUN, uid: 'F/s', feature: 'F', scenario: 's', attempt: 1, at: '2026-07-31T10:00:01Z',
    })
    store.handleStepStarted({ runId: RUN, uid: 'F/s', stepId: 's1', kind: 'Given', text: 'a thing' })

    await hydrateFromServer(
      fakeFetch({
        '/api/runs': { ok: true, body: JSON.stringify([{ runId: RUN, orphaned: false }]) },
        [`/api/runs/${RUN}/export?format=ndjson`]: { ok: true, body: finishedRunNdjson },
      }),
    )

    const scenario = store.runById(RUN)!.scenarios['F/s']!
    expect(scenario.steps).toHaveLength(1)
    expect(scenario.steps[0]!.status).toBe('passed')
  })

  it('prunes local runs the server no longer knows', async () => {
    const store = useRunsStore()
    store.handleRunHeartbeat({ runId: 'dead-local-run', at: '2026-07-31T10:00:00Z' })

    await hydrateFromServer(
      fakeFetch({
        '/api/runs': { ok: true, body: JSON.stringify([]) },
      }),
    )

    expect(store.allRuns).toHaveLength(0)
  })

  it('is best-effort: a failing server leaves the store untouched', async () => {
    const store = useRunsStore()
    store.handleRunHeartbeat({ runId: RUN, at: '2026-07-31T10:00:00Z' })

    await hydrateFromServer(fakeFetch({}))
    await hydrateFromServer((async () => {
      throw new Error('down')
    }) as unknown as typeof fetch)

    expect(store.runById(RUN)).toBeDefined()
  })

  it('a torn archive line does not sink the rest of the replay', async () => {
    await hydrateFromServer(
      fakeFetch({
        '/api/runs': { ok: true, body: JSON.stringify([{ runId: RUN, orphaned: false }]) },
        [`/api/runs/${RUN}/export?format=ndjson`]: {
          ok: true,
          body: '{"type":"run_hea\n' + finishedRunNdjson,
        },
      }),
    )

    expect(useRunsStore().runById(RUN)?.finished).toBe(true)
  })
})
