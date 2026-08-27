import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { relayToStore } from '../relayToStore'
import { useEventModelStore } from '@/stores/event-model-store'

/**
 * Issue #169 — a pushed Event Model redraws the page without an F5.
 *
 * `PUT /api/event-model` stored the descriptor, but the page read `GET /api/event-model` on load
 * ONLY, so even a successful push needed a manual refresh. That was the last gap between "I edited
 * a handler" and "the diagram is right"; paired with Wolverine's `event-model --url`,
 * `dotnet watch run -- event-model --url …` now redraws on every save with no keystrokes.
 */
describe('#169 — event_model_changed refreshes the Event Model store', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('re-fetches on the push', () => {
    const store = useEventModelStore()
    const refresh = vi.spyOn(store, 'refresh').mockResolvedValue(undefined)

    relayToStore({ type: 'event_model_changed', data: { name: 'Wallets' } })

    expect(refresh).toHaveBeenCalledOnce()
  })

  it('ignores an unrelated message', () => {
    const store = useEventModelStore()
    const refresh = vi.spyOn(store, 'refresh').mockResolvedValue(undefined)

    relayToStore({ type: 'run_heartbeat', data: { runId: 'x' } })

    expect(refresh).not.toHaveBeenCalled()
  })
})

describe('#169 — refresh() is the quiet twin of load()', () => {
  beforeEach(() => setActivePinia(createPinia()))

  const descriptor = { name: 'Wallets', slices: [] }

  function ok(body: unknown) {
    return vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => body }) as never
  }

  it('never shows a spinner — the page already has a diagram on screen', async () => {
    const store = useEventModelStore()
    await store.load(ok(descriptor))
    expect(store.status).toBe('loaded')

    // A producer saving a file must not make a good diagram flash a loading state.
    const seen: string[] = []
    const slowFetch = vi.fn().mockImplementation(async () => {
      seen.push(store.status)
      return { ok: true, status: 200, json: async () => ({ name: 'Orders', slices: [] }) }
    }) as never

    await store.refresh(slowFetch)

    expect(seen).toEqual(['loaded'])
    expect(store.descriptor?.name).toBe('Orders')
  })

  it('keeps the previous descriptor when a refresh fails', async () => {
    // A dropped refresh is not worth blanking a good diagram; the next push corrects it.
    const store = useEventModelStore()
    await store.load(ok(descriptor))

    await store.refresh(vi.fn().mockRejectedValue(new Error('offline')) as never)
    expect(store.descriptor?.name).toBe('Wallets')
    expect(store.status).toBe('loaded')

    await store.refresh(vi.fn().mockResolvedValue({ ok: false, status: 500 }) as never)
    expect(store.descriptor?.name).toBe('Wallets')
    expect(store.status).toBe('loaded')
  })

  it('DOES apply a 404 — a deleted model is an answer, not a failure', async () => {
    const store = useEventModelStore()
    await store.load(ok(descriptor))

    await store.refresh(vi.fn().mockResolvedValue({ ok: false, status: 404 }) as never)

    expect(store.descriptor).toBeNull()
    expect(store.status).toBe('absent')
  })
})
