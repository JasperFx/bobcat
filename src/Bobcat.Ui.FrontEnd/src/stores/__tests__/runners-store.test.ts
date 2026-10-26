import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useRunnersStore } from '../runners-store'

/** bobcat#784 — one HTTP load, then pushes; and a console that cannot answer leaves what the page has. */
describe('runners-store', () => {
  beforeEach(() => setActivePinia(createPinia()))

  const fetching = (answers: Record<string, unknown>) =>
    (async (input: unknown) => {
      const body = answers[String(input)]
      return body === undefined
        ? ({ ok: false, json: async () => null } as Response)
        : ({ ok: true, json: async () => body } as Response)
    }) as typeof fetch

  it('loads the runners and the builds once, from their two routes', async () => {
    const store = useRunnersStore()
    await store.load(fetching({ '/api/runners': [{ runnerId: 'r1', commands: [] }], '/api/build-status': [{ suite: 'S', state: 'built' }] }))

    expect(store.runners?.map((r) => r.runnerId)).toEqual(['r1'])
    expect(store.builds?.map((b) => b.state)).toEqual(['built'])
    expect(store.runnerById('r1')).toBeDefined()
  })

  it('a console that does not answer reads as no runners on the first load, and as what it had after', async () => {
    const store = useRunnersStore()
    await store.load(fetching({}))
    expect(store.runners).toEqual([])
    expect(store.builds).toEqual([])

    store.handleRunnersChanged({ runners: [{ runnerId: 'r1' } as never] })
    await store.load(fetching({}))
    expect(store.runners?.map((r) => r.runnerId)).toEqual(['r1'])
  })
})
