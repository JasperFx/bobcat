import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { relayToStore } from '../relayToStore'
import { useRunsStore } from '@/stores/runs-store'

/**
 * Issue #84 — the generated relayToStore cases for the supervisor topology events route to the
 * runs store, through the same batched frame and archived-line shapes the other events use.
 */
const RUN = '9a1f1a1e-0000-0000-0000-000000000085'

describe('relayToStore supervisor topology', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('dispatches lane, recycle and worker-fault envelopes from one batched frame', () => {
    relayToStore({
      type: 'batched_web_socket_payload',
      data: {
        items: [
          {
            type: 'run_started',
            data: {
              runId: RUN,
              suite: 'Supervised Suite',
              repository: '/repo',
              branch: 'main',
              mode: 'supervised',
              startedAt: '2026-08-21T10:00:00Z',
              totalScenarios: 2,
              tag: null,
            },
          },
          { type: 'lane_started', data: { runId: RUN, lane: 0, uids: ['F/a'], at: '2026-08-21T10:00:01Z' } },
          { type: 'lane_started', data: { runId: RUN, lane: 1, uids: ['F/b'], at: '2026-08-21T10:00:01Z' } },
          { type: 'resource_recycled', data: { runId: RUN, resource: 'rabbit', at: '2026-08-21T10:00:02Z' } },
          {
            type: 'lane_finished',
            data: { runId: RUN, lane: 1, outcomes: 0, crashed: true, at: '2026-08-21T10:00:03Z' },
          },
          {
            type: 'worker_faulted',
            data: {
              runId: RUN,
              lane: 1,
              fault: 'the worker exited with code 134',
              exitCode: 134,
              standardError: 'Aborted',
              at: '2026-08-21T10:00:03Z',
            },
          },
        ],
      },
    })

    const run = useRunsStore().runById(RUN)!
    expect(run.lanes.map((l) => [l.lane, l.status])).toEqual([
      [0, 'running'],
      [1, 'crashed'],
    ])
    expect(run.recycles).toEqual([{ resource: 'rabbit', at: '2026-08-21T10:00:02Z' }])
    expect(run.faults[0]).toMatchObject({ lane: 1, exitCode: 134, standardError: 'Aborted' })
  })

  it('handles an archived NDJSON line wrapped by hydration, discriminator included', () => {
    // hydrateFromServer wraps each archived line as { type, data: line } and the line itself
    // carries the STJ `type` discriminator — the same shape an unwrapped batch item has.
    const line = {
      type: 'worker_faulted',
      runId: RUN,
      lane: null,
      fault: 'the worker stopped responding but is still running (connection closed)',
      exitCode: null,
      standardError: null,
      at: '2026-08-21T10:00:09Z',
    }
    relayToStore({ type: line.type, data: line })

    const run = useRunsStore().runById(RUN)!
    expect(run.faults).toHaveLength(1)
    expect(run.faults[0]!.lane).toBeNull()
    expect(run.faults[0]!.exitCode).toBeNull()
  })
})
