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

  // bobcat#799: the push names its model, and the store decides whether that is the one on screen.
  it('hands the push, with the model it names, to the store', () => {
    const store = useEventModelStore()
    const changed = vi.spyOn(store, 'modelChanged').mockResolvedValue(undefined)

    relayToStore({ type: 'event_model_changed', data: { name: 'Wallets' } })

    expect(changed).toHaveBeenCalledOnce()
    expect(changed).toHaveBeenCalledWith('Wallets')
  })

  it('ignores an unrelated message', () => {
    const store = useEventModelStore()
    const changed = vi.spyOn(store, 'modelChanged').mockResolvedValue(undefined)

    relayToStore({ type: 'run_heartbeat', data: { runId: 'x' } })

    expect(changed).not.toHaveBeenCalled()
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

/**
 * bobcat#799 — a console holds several models. A push of one that is not on screen moves the listing
 * (its push time, perhaps a new entry in the switcher) and leaves the diagram alone; a push of the one on
 * screen redraws it as #169 always did.
 */
describe('#799 — modelChanged redraws only the model on screen', () => {
  beforeEach(() => setActivePinia(createPinia()))

  const listing = { current: 'BankAccount', preferred: 'BankAccount', models: [] }

  function consoleFetch(): ReturnType<typeof vi.fn> {
    return vi.fn().mockImplementation(async (url: string) => {
      if (url === '/api/event-models') return { ok: true, status: 200, json: async () => listing }
      const name = new URL(url, 'http://console').searchParams.get('name') ?? 'BankAccount'
      return { ok: true, status: 200, json: async () => ({ name, slices: [] }) }
    })
  }

  it('re-reads the shown model when the push was of it', async () => {
    const store = useEventModelStore()
    const fetch = consoleFetch()
    await store.select('CritterCrush', fetch as never)
    await store.loadModels(fetch as never)
    fetch.mockClear()

    await store.modelChanged('CritterCrush', fetch as never)

    expect(fetch.mock.calls.map((c) => c[0])).toEqual(
      expect.arrayContaining(['/api/event-model?name=CritterCrush', '/api/event-models'])
    )
    expect(store.descriptor?.name).toBe('CritterCrush')
  })

  it("does not swap the diagram for another application's push, but refreshes the listing", async () => {
    const store = useEventModelStore()
    const fetch = consoleFetch()
    await store.select('CritterCrush', fetch as never)
    await store.loadModels(fetch as never)
    fetch.mockClear()

    await store.modelChanged('BankAccount', fetch as never)

    expect(fetch.mock.calls.map((c) => c[0])).toEqual(['/api/event-models'])
    expect(store.descriptor?.name).toBe('CritterCrush')
  })

  it('a store reading the current model re-reads on any push, since any push can change which is current', async () => {
    const store = useEventModelStore()
    const fetch = consoleFetch()
    await store.load(fetch as never)
    fetch.mockClear()

    await store.modelChanged('BankAccount', fetch as never)

    expect(fetch.mock.calls.map((c) => c[0])).toEqual(['/api/event-model'])
  })
})
