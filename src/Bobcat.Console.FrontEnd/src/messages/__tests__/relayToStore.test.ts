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

  it('handles the real Wolverine.SignalR CloudEvents envelope, which arrives as a JSON string', () => {
    // Captured verbatim from a live Bobcat.Console host (wiretap, 2026-07-31): the hub method
    // is ReceiveMessage and the payload is a CloudEvents JSON *string* whose `type` is the
    // snake_case Wolverine message type name and whose `data` is the event. The extra
    // CloudEvents fields (specversion, traceparent, ...) must be ignored, not fatal.
    const envelope =
      '{"topic":null,"tenantid":null,"traceid":"a720e7b0b2cf1748ac2b9f47bd4e9b7f","tracestate":null,' +
      '"data":{"type":"run_started","suite":"WireTap","repository":"/tmp/x","branch":"main",' +
      '"mode":"in-process","startedAt":"2026-07-31T15:28:01.681+00:00","totalScenarios":1,' +
      `"runId":"${RUN}"},` +
      '"id":"08deef18-4f79-7870-baa4-8b734d760000","specversion":"1.0",' +
      '"datacontenttype":"application/json; charset=utf-8","source":"Bobcat.Console",' +
      '"type":"run_started","time":"2026-07-31T15:28:02.4803540\\u002B00:00",' +
      '"traceparent":"00-a720e7b0b2cf1748ac2b9f47bd4e9b7f-95641f2b5899e163-00"}'

    relayToStore(envelope)

    const store = useRunsStore()
    const run = store.runById(RUN)
    expect(run?.suite).toBe('WireTap')
    expect(run?.totalScenarios).toBe(1)
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

  it('unwraps a batched_web_socket_payload and dispatches every inner item in order', () => {
    // Shape pinned on the .NET side by SignalRBatchingTests: the CloudEvents `data` of a
    // batched frame is { items: [{ type, data }, ...] } with web-defaults camelCase.
    relayToStore({
      type: 'batched_web_socket_payload',
      data: {
        items: [
          {
            type: 'run_started',
            data: {
              runId: RUN,
              suite: 'Batched Suite',
              repository: '/repo',
              branch: 'main',
              mode: 'in-process',
              startedAt: '2026-07-31T10:00:00Z',
              totalScenarios: 1,
            },
          },
          {
            type: 'scenario_started',
            data: {
              runId: RUN,
              uid: 'Calc/adds',
              feature: 'Calc',
              scenario: 'adds',
              attempt: 1,
              at: '2026-07-31T10:00:01Z',
            },
          },
          {
            type: 'scenario_finished',
            data: {
              runId: RUN,
              uid: 'Calc/adds',
              outcome: 'CleanPass',
              attempts: 1,
              durationMs: 20,
              errorMessage: null,
            },
          },
        ],
      },
    })

    const store = useRunsStore()
    const run = store.runById(RUN)
    expect(run?.suite).toBe('Batched Suite')
    expect(run?.scenarios['Calc/adds']?.outcome).toBe('CleanPass')
  })

  it('tolerates a batched payload with no items', () => {
    expect(() => relayToStore({ type: 'batched_web_socket_payload', data: {} })).not.toThrow()
    expect(() => relayToStore({ type: 'batched_web_socket_payload', data: null })).not.toThrow()
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
