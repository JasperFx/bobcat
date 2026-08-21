import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { relayToStore } from '../relayToStore'
import { useRunsStore } from '@/stores/runs-store'

/** The step_progress envelope dispatches, bare and inside a server-side batch. */
const RUN = '9a1f1a1e-0000-0000-0000-000000000099'
const UID = 'Customers/Bulk import'

describe('relayToStore step_progress', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('dispatches a bare step_progress envelope', () => {
    relayToStore({
      type: 'scenario_started',
      data: { runId: RUN, uid: UID, feature: 'Customers', scenario: 'Bulk import', attempt: 1, at: '2026-08-21T10:00:01Z', totalSteps: 1 },
    })
    relayToStore({
      type: 'step_started',
      data: { runId: RUN, uid: UID, stepId: 'grammar', kind: 'Given', text: 'rows', stepNumber: 1, totalSteps: 1, scenarioElapsedMs: 0 },
    })
    relayToStore({
      type: 'step_progress',
      data: { runId: RUN, uid: UID, stepId: 'grammar', message: null, row: 7, totalRows: 20, elapsedMs: 90 },
    })

    const step = useRunsStore().runById(RUN)!.scenarios[UID]!.steps[0]!
    expect(step.progress).toEqual({ message: null, row: 7, totalRows: 20, elapsedMs: 90 })
  })

  it('dispatches step_progress items inside a batched frame in order', () => {
    relayToStore({
      type: 'batched_web_socket_payload',
      data: {
        items: [
          { type: 'step_started', data: { runId: RUN, uid: UID, stepId: 'wait', kind: 'Then', text: 'drains' } },
          { type: 'step_progress', data: { runId: RUN, uid: UID, stepId: 'wait', message: 'waiting… (attempt 1, 10ms)', row: null, totalRows: null, elapsedMs: 10 } },
          { type: 'step_progress', data: { runId: RUN, uid: UID, stepId: 'wait', message: 'waiting… (attempt 2, 20ms)', row: null, totalRows: null, elapsedMs: 20 } },
        ],
      },
    })

    const step = useRunsStore().runById(RUN)!.scenarios[UID]!.steps[0]!
    expect(step.progress?.message).toBe('waiting… (attempt 2, 20ms)')
  })
})
