import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { relayToStore } from '../relayToStore'
import { useRunnersStore } from '@/stores/runners-store'
import { useRunsStore } from '@/stores/runs-store'

/**
 * bobcat#784 — the console's own notices, which no publisher sends: an orphaned run, the runner
 * list, the build reports. Each arrives on the same hub as the events, inside the batched frame,
 * and the page applies it instead of polling for it.
 */
const RUN = '9a1f1a1e-0000-0000-0000-000000000784'

describe('relayToStore — the console’s notices', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('run_orphaned marks a live run orphaned, and a later event for it un-orphans it', () => {
    const store = useRunsStore()
    relayToStore({
      type: 'run_started',
      data: { runId: RUN, suite: 'S', repository: '/repo', branch: null, mode: 'in-process', startedAt: '2026-10-26T10:00:00Z', totalScenarios: 2 },
    })

    relayToStore({ type: 'run_orphaned', data: { runId: RUN, lastEventAt: '2026-10-26T10:00:30Z' } })
    expect(store.runById(RUN)!.orphaned).toBe(true)
    expect(store.runById(RUN)!.lastEventAt).toBe('2026-10-26T10:00:30Z')

    // The publisher was only slow: its next heartbeat is the proof, as it is for the registry.
    relayToStore({ type: 'run_heartbeat', data: { runId: RUN, at: '2026-10-26T10:01:10Z' } })
    expect(store.runById(RUN)!.orphaned).toBe(false)
  })

  it('run_orphaned for a run the board has not heard of makes no card', () => {
    relayToStore({ type: 'run_orphaned', data: { runId: 'unknown', lastEventAt: '2026-10-26T10:00:30Z' } })
    expect(useRunsStore().allRuns).toHaveLength(0)
  })

  it('runners_changed and build_status_changed replace what the page has, inside a batched frame', () => {
    const runners = useRunnersStore()
    relayToStore({
      type: 'batched_web_socket_payload',
      data: {
        items: [
          {
            type: 'runners_changed',
            data: {
              runners: [{
                runnerId: 'r1', repository: '/code/bank', repositorySlug: 'JasperFx/bank', branch: 'main', suite: 'Bank.Tests',
                lane: 'projected', modes: ['cold'], specs: 3, connected: true, status: null, statusDetail: null,
                registeredAt: '2026-10-26T10:00:00Z', lastSeenAt: '2026-10-26T10:00:00Z', commands: [], builtAt: null,
              }],
            },
          },
          {
            type: 'build_status_changed',
            data: { builds: [{ suite: 'Bank.Tests', repository: '/code/bank', branch: 'main', state: 'build-failed', detail: 'error CS1002', at: '2026-10-26T10:00:01Z' }] },
          },
        ],
      },
    })

    expect(runners.runners?.map((r) => [r.runnerId, r.connected])).toEqual([['r1', true]])
    expect(runners.builds?.map((b) => [b.suite, b.state])).toEqual([['Bank.Tests', 'build-failed']])

    relayToStore({ type: 'runners_changed', data: { runners: [] } })
    expect(runners.runners).toEqual([])
  })
})
