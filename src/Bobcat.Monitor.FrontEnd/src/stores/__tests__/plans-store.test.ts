import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { usePlansStore } from '../plans-store'

function fetchReturning(status: number, body: unknown): typeof fetch {
  return (() =>
    Promise.resolve({
      ok: status >= 200 && status < 300,
      status,
      json: () => Promise.resolve(body),
    } as Response)) as typeof fetch
}

const failingFetch: typeof fetch = () => Promise.reject(new Error('backend down'))

describe('plans-store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('folds the plan list and finds summaries by slug', async () => {
    const store = usePlansStore()
    await store.fetchPlans(
      fetchReturning(200, [
        {
          slug: 'epic',
          title: 'The Epic',
          source: 'file',
          sourcePath: '/plans/epic.yaml',
          valid: true,
          nodes: 5,
          errors: [],
          loadedAt: '2026-08-01T10:00:00Z',
        },
      ]),
    )

    expect(store.allPlans).toHaveLength(1)
    expect(store.summaryOf('epic')!.title).toBe('The Epic')
    expect(store.summaryOf('nope')).toBeNull()
  })

  it('folds a status payload and clears any prior invalid marker', async () => {
    const store = usePlansStore()
    store.invalid = { epic: ['old error'] }

    await store.fetchStatus(
      'epic',
      fetchReturning(200, {
        slug: 'epic',
        title: 'The Epic',
        nodes: [
          {
            id: 'a',
            kind: 'issue',
            title: 'a',
            status: 'open',
            ready: true,
            ref: 'o/r#1',
            detail: null,
            observedTitle: null,
            assignees: null,
            openPrs: null,
            observedAt: null,
            dependsOn: [],
            runId: null,
          },
        ],
        ready: ['a'],
      }),
    )

    expect(store.statusOf('epic')!.ready).toEqual(['a'])
    expect(store.invalid).toEqual({})
  })

  it('a 409 records the document errors instead of a status', async () => {
    const store = usePlansStore()
    await store.fetchStatus('broken', fetchReturning(409, { errors: ['no nodes'] }))

    expect(store.statusOf('broken')).toBeNull()
    expect(store.invalid['broken']).toEqual(['no nodes'])
  })

  it('a dead backend keeps the last known state rather than blanking the view', async () => {
    const store = usePlansStore()
    await store.fetchStatus(
      'epic',
      fetchReturning(200, { slug: 'epic', title: 't', nodes: [], ready: [] }),
    )

    await store.fetchPlans(failingFetch)
    await store.fetchStatus('epic', failingFetch)

    expect(store.statusOf('epic')).not.toBeNull()
  })
})
