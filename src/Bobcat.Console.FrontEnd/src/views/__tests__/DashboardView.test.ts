import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { ElMessageBox } from 'element-plus'
import { useRunsStore } from '@/stores/runs-store'
import DashboardView from '../DashboardView.vue'

/**
 * Issues #196 and #197 — the run card's age, and clearing a board that has accumulated. The
 * observed case both were written for is a shared console carrying 46 runs from four
 * repositories and worktrees, several days old, all visually identical.
 */
const routerLinkStub = { template: '<a><slot /></a>', props: ['to'] }

type Store = ReturnType<typeof useRunsStore>

function finishedRun(store: Store, runId: string, suite: string, startedAt: string) {
  store.handleRunStarted({
    runId,
    suite,
    repository: '/repo',
    branch: 'main',
    mode: 'in-process',
    startedAt,
    totalScenarios: 1,
  })
  store.handleRunFinished({
    runId,
    exitCode: 0,
    passed: 1,
    failed: 0,
    passedOnRetry: 0,
    indeterminate: 0,
    finishedAt: new Date(Date.parse(startedAt) + 90_000).toISOString(),
  })
}

function liveRun(store: Store, runId: string, suite: string, startedAt: string) {
  store.handleRunStarted({
    runId,
    suite,
    repository: '/repo',
    branch: 'main',
    mode: 'supervised',
    startedAt,
    totalScenarios: 100,
  })
}

function render() {
  return mount(DashboardView, { global: { stubs: { RouterLink: routerLinkStub } } })
}

describe('DashboardView', () => {
  let store: Store
  let deleted: string[]

  beforeEach(() => {
    setActivePinia(createPinia())
    store = useRunsStore()
    deleted = []
    vi.useFakeTimers()
    vi.setSystemTime(Date.parse('2026-09-01T12:00:00Z'))
    // Stands in for the registry, which takes only the runs nothing is publishing to and
    // reports exactly which ones it took — the UI drops those, never what it predicted.
    vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
      if (init?.method === 'DELETE') deleted.push(url)
      const taken = store.allRuns.filter((run) => run.finished || run.orphaned).map((run) => run.runId)
      return Promise.resolve({
        ok: true,
        json: () => Promise.resolve({ count: taken.length, runIds: taken }),
      } as Response)
    })
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
  })

  it('says how old each card is, and how long a finished run took', () => {
    finishedRun(store, 'r1', 'Recent', '2026-09-01T11:55:00Z')
    const text = render().text()

    // Anchored on the finish for a done run: "4m ago" as of the fake clock, not "5m".
    expect(text).toContain('3m ago')
    expect(text).toContain('took 1m30s')
  })

  it('says a live run started, because the same words mean two things otherwise', () => {
    liveRun(store, 'r1', 'Gate', '2026-09-01T11:54:00Z')
    expect(render().text()).toContain('started 6m ago')
  })

  it('orders the board newest first rather than by whatever arrived when', () => {
    finishedRun(store, 'old', 'Oldest', '2026-08-28T12:00:00Z')
    finishedRun(store, 'new', 'Newest', '2026-09-01T11:50:00Z')
    finishedRun(store, 'mid', 'Middle', '2026-08-31T12:00:00Z')

    const suites = render()
      .findAll('.bm-run-title')
      .map((link) => link.text())
    expect(suites).toEqual(['Newest', 'Middle', 'Oldest'])
  })

  it('offers a bulk eject only once there is more than one card to take', () => {
    finishedRun(store, 'r1', 'One', '2026-09-01T11:50:00Z')
    expect(render().find('.bm-eject-all').exists()).toBe(false)

    finishedRun(store, 'r2', 'Two', '2026-09-01T11:51:00Z')
    expect(render().find('.bm-eject-all').text()).toContain('2')
  })

  it('confirms with the count and what survives, then clears the board', async () => {
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    finishedRun(store, 'r1', 'One', '2026-09-01T11:50:00Z')
    finishedRun(store, 'r2', 'Two', '2026-09-01T11:51:00Z')

    const view = render()
    await view.find('.bm-eject-all').trigger('click')
    await vi.waitFor(() => expect(deleted.length).toBe(1))

    const [message] = confirm.mock.calls[0]
    expect(message).toContain('Clear 2 runs')
    // The whole reason this control is offerable: it clears a board, it does not delete evidence.
    expect(message).toContain('archives are kept on disk')
    // A bulk action on a shared surface is never the default-focused button.
    expect(confirm.mock.calls[0][2]).toMatchObject({ autofocus: false })

    expect(deleted[0]).toBe('/api/runs?')
    expect(store.allRuns).toHaveLength(0)
  })

  it('takes nothing when the confirm is dismissed', async () => {
    vi.spyOn(ElMessageBox, 'confirm').mockRejectedValue(new Error('cancel'))
    finishedRun(store, 'r1', 'One', '2026-09-01T11:50:00Z')
    finishedRun(store, 'r2', 'Two', '2026-09-01T11:51:00Z')

    const view = render()
    await view.find('.bm-eject-all').trigger('click')
    await vi.waitFor(() => expect(store.allRuns).toHaveLength(2))
    expect(deleted).toHaveLength(0)
  })

  it('leaves a live run alone and says so, because ejecting one does not stick', async () => {
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    finishedRun(store, 'done', 'Done', '2026-09-01T11:50:00Z')
    finishedRun(store, 'done2', 'AlsoDone', '2026-09-01T11:51:00Z')
    liveRun(store, 'live', 'Gate', '2026-09-01T11:52:00Z')

    const view = render()
    await view.find('.bm-eject-all').trigger('click')
    await vi.waitFor(() => expect(store.allRuns).toHaveLength(1))

    expect(confirm.mock.calls[0][0]).toContain('1 still running — those stay.')
    expect(store.allRuns[0].runId).toBe('live')
  })
})
