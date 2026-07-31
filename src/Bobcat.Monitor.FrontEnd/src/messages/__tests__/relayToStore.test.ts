import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { relayToStore } from '../relayToStore'
import { useRunsStore } from '@/stores/runs-store'

const RUN = '9a1f1a1e-0000-0000-0000-000000000002'

describe('relayToStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('dispatches a run_started envelope to the runs store', () => {
    relayToStore({
      type: 'run_started',
      data: {
        runId: RUN,
        suite: 'Bobcat Acceptance',
        repository: '/Users/dev/code/bobcat',
        branch: 'main',
        mode: 'in-process',
        startedAt: '2026-07-31T10:00:00Z',
        totalScenarios: 12,
      },
    })

    const store = useRunsStore()
    expect(store.runById(RUN)?.suite).toBe('Bobcat Acceptance')
  })

  it('parses string payloads before dispatching', () => {
    relayToStore(
      JSON.stringify({
        type: 'run_heartbeat',
        data: { runId: RUN, at: '2026-07-31T10:00:30Z' },
      }),
    )

    expect(useRunsStore().runById(RUN)).toBeDefined()
  })

  it('ignores unknown message types without throwing', () => {
    expect(() => relayToStore({ type: 'not_a_real_message', data: {} })).not.toThrow()
    expect(useRunsStore().allRuns).toHaveLength(0)
  })

  it('ignores malformed payloads without throwing', () => {
    expect(() => relayToStore(42)).not.toThrow()
    expect(() => relayToStore({ noType: true })).not.toThrow()
  })
})
