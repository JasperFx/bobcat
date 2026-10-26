import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { ElMessageBox } from 'element-plus'
import { useRunsStore } from '@/stores/runs-store'
import { useBoardStore } from '@/stores/board-store'
import { resetRunners } from '@/composables/useRunners'
import { useRunnersStore } from '@/stores/runners-store'
import type { RunnerView } from '@/lib/runners'
import DashboardView from '../DashboardView.vue'

/**
 * Issues #196 and #197 — the run card's age, and clearing a board that has accumulated — and
 * bobcat#786, which grouped the board by suite: the observed case for all three is a shared console
 * carrying 46 runs from four repositories and worktrees, several days old, all visually identical,
 * one busy suite burying every other.
 */
const routerLinkStub = { template: '<a><slot /></a>', props: ['to'] }
// element-plus's select and dropdown recurse on a sibling's re-render under happy-dom (not in a
// browser); the filter is set through the board store here, which is what the view reads anyway.
const stubs = { RouterLink: routerLinkStub, teleport: true, ElSelect: true, ElOption: true, ElDropdown: true, ElDropdownMenu: true, ElDropdownItem: true }

type Store = ReturnType<typeof useRunsStore>

function finishedRun(store: Store, runId: string, suite: string, startedAt: string, repository = '/repo', failed = 0) {
  store.handleRunStarted({ runId, suite, repository, branch: 'main', mode: 'in-process', startedAt, totalScenarios: 1 + failed })
  store.handleRunFinished({
    runId, exitCode: failed > 0 ? 1 : 0, passed: 1, failed, passedOnRetry: 0, indeterminate: 0,
    finishedAt: new Date(Date.parse(startedAt) + 90_000).toISOString(),
  })
}

function liveRun(store: Store, runId: string, suite: string, startedAt: string, repository = '/repo') {
  store.handleRunStarted({ runId, suite, repository, branch: 'main', mode: 'supervised', startedAt, totalScenarios: 100 })
}

function runner(overrides: Partial<RunnerView> = {}): RunnerView {
  return {
    runnerId: 'r1', repository: '/repo', repositorySlug: null, branch: 'main', suite: 'Bank.Tests', lane: 'projected',
    modes: ['cold', 'warm'], specs: 22, connected: true, status: null, statusDetail: null, registeredAt: '', lastSeenAt: '',
    commands: [], ...overrides,
  }
}

function render() {
  return mount(DashboardView, { global: { stubs } })
}

const suiteNames = (page: ReturnType<typeof render>) => page.findAll('[data-testid="suite-name"]').map((s) => s.text())

async function expand(page: ReturnType<typeof render>, index = 0) {
  await page.findAll('[data-testid="suite-toggle"]')[index]!.trigger('click')
  await flushPromises()
}

describe('DashboardView', () => {
  let store: Store
  let deleted: string[]
  let registered: RunnerView[]
  let retention: { runs: number; days: number } | null

  beforeEach(() => {
    setActivePinia(createPinia())
    store = useRunsStore()
    resetRunners()
    localStorage.clear()
    deleted = []
    registered = []
    retention = null
    vi.useFakeTimers()
    vi.setSystemTime(Date.parse('2026-09-01T12:00:00Z'))
    // Stands in for the registry, which takes only the runs nothing is publishing to and
    // reports exactly which ones it took — the UI drops those, never what it predicted.
    vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
      if (init?.method === 'DELETE') deleted.push(url)
      if (url === '/api/runners') return Promise.resolve({ ok: true, json: () => Promise.resolve(registered) } as Response)
      if (url === '/api/retention') return Promise.resolve({ ok: retention !== null, json: () => Promise.resolve(retention) } as Response)
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
    resetRunners()
  })

  describe('grouped by suite (bobcat#786)', () => {
    it('marks a suite whose build is broken on its own row, matched by suite name and checkout (bobcat#795)', async () => {
      finishedRun(store, 'r1', 'Busy.Suite', '2026-09-01T11:40:00Z')
      finishedRun(store, 'r2', 'Quiet.Suite', '2026-09-01T11:00:00Z')
      const runnersStore = useRunnersStore()
      runnersStore.runners = []
      runnersStore.builds = [
        { suite: 'Busy.Suite', repository: '/repo', branch: 'main', state: 'build-failed', detail: 'Specs.cs(3,1): error CS1002', at: '2026-09-01T11:59:00Z' },
      ]
      const page = render()
      await flushPromises()

      const chips = page.findAll('[data-testid="suite-build"]')
      expect(chips).toHaveLength(1)
      expect(chips[0]!.text()).toBe('build failed')
      expect(chips[0]!.attributes('title')).toContain('CS1002')
      const rows = page.findAll('[data-testid="suite-row"]')
      expect(rows[0]!.find('[data-testid="suite-build"]').exists()).toBe(true)
      expect(rows[1]!.find('[data-testid="suite-build"]').exists()).toBe(false)
    })

    it('shows one row per suite with its latest result, however many runs it has', async () => {
      finishedRun(store, 'r1', 'Busy.Suite', '2026-09-01T11:40:00Z')
      finishedRun(store, 'r2', 'Busy.Suite', '2026-09-01T11:50:00Z', '/repo', 2)
      finishedRun(store, 'r3', 'Busy.Suite', '2026-09-01T11:55:00Z')
      finishedRun(store, 'r4', 'Quiet.Suite', '2026-09-01T11:00:00Z')
      const page = render()
      await flushPromises()

      expect(suiteNames(page)).toEqual(['Busy.Suite', 'Quiet.Suite'])
      const latest = page.findAll('[data-testid="suite-latest"]')
      // Anchored on the finish for a done run: "3m ago" as of the fake clock, not "5m".
      expect(latest[0]!.text()).toBe('1 passed · 3m ago')
      expect(latest[0]!.attributes('data-tone')).toBe('passed')
      expect(page.findAll('[data-testid="history-segment"]')).toHaveLength(4)
      expect(page.findAll('[data-testid="run-card"]')).toHaveLength(0)
    })

    it('expands a suite to its runs, newest first, and remembers that for this viewer', async () => {
      finishedRun(store, 'r1', 'Busy.Suite', '2026-09-01T11:40:00Z')
      finishedRun(store, 'r2', 'Busy.Suite', '2026-09-01T11:50:00Z')
      const page = render()
      await flushPromises()

      await expand(page)
      const cards = page.findAll('[data-testid="run-card"]')
      expect(cards).toHaveLength(2)
      expect(cards[0]!.find('[data-testid="run-link"]').text()).toBe('8m ago')
      expect(cards[0]!.text()).toContain('took 1m30s')
      expect(cards[1]!.find('[data-testid="run-link"]').text()).toBe('18m ago')
      expect(JSON.parse(localStorage.getItem('bobcat.board.expanded')!)).toEqual(['/repo::Busy.Suite'])

      await expand(page)
      expect(page.findAll('[data-testid="run-card"]')).toHaveLength(0)
    })

    it('sorts a live suite first, and says its run started rather than merely how old it is', async () => {
      finishedRun(store, 'new', 'Newest', '2026-09-01T11:50:00Z')
      liveRun(store, 'live', 'Gate', '2026-09-01T11:54:00Z')
      finishedRun(store, 'old', 'Oldest', '2026-08-28T12:00:00Z')
      finishedRun(store, 'mid', 'Middle', '2026-08-31T12:00:00Z')
      liveRun(store, 'older-live', 'Slow.Gate', '2026-08-30T12:00:00Z')
      const page = render()
      await flushPromises()

      expect(suiteNames(page)).toEqual(['Gate', 'Slow.Gate', 'Newest', 'Middle', 'Oldest'])
      expect(page.findAll('[data-testid="suite-row"]')[0]!.attributes('data-live')).toBe('true')
      expect(page.findAll('[data-testid="live-progress"]')).toHaveLength(2)

      await expand(page)
      expect(page.find('[data-testid="run-card"] [data-testid="run-link"]').text()).toBe('started 6m ago')
    })

    it('shows a live run\'s overall progress under its row: done of planned, counts, elapsed, an estimate, and the colour rule', async () => {
      finishedRun(store, 'previous', 'Gate', '2026-09-01T11:00:00Z')
      store.handleRunFinished({ runId: 'previous', exitCode: 0, passed: 100, failed: 0, passedOnRetry: 0, indeterminate: 0, finishedAt: '2026-09-01T11:10:00Z' })
      liveRun(store, 'live', 'Gate', '2026-09-01T11:56:00Z')
      for (const uid of ['a', 'b', 'c']) {
        store.handleScenarioStarted({ runId: 'live', uid, feature: 'F', scenario: uid, attempt: 1, at: '2026-09-01T11:57:00Z' })
        store.handleScenarioFinished({ runId: 'live', uid, outcome: 'CleanPass', attempts: 1, durationMs: 5, errorMessage: null })
      }
      const page = render()
      await flushPromises()

      const live = page.find('[data-testid="live-progress"]')
      expect(live.attributes('data-tone')).toBe('green')
      expect(live.find('[data-testid="live-counts"]').text()).toBe('3 / 100 · 3 passed')
      expect(live.find('[role="progressbar"]').attributes('aria-valuenow')).toBe('3')
      expect(live.find('[data-testid="live-elapsed"]').text()).toBe('4m00s elapsed')
      // The previous run took ten minutes; four have elapsed.
      expect(live.find('[data-testid="live-eta"]').text()).toBe('about 6m left')
      expect(live.find('[data-testid="go-to-running"]').exists()).toBe(true)
      expect(live.text()).not.toContain('Stop')

      store.handleScenarioStarted({ runId: 'live', uid: 'd', feature: 'F', scenario: 'd', attempt: 1, at: '2026-09-01T11:58:00Z' })
      store.handleScenarioFinished({ runId: 'live', uid: 'd', outcome: 'Failed', attempts: 1, durationMs: 5, errorMessage: 'no' })
      await flushPromises()
      expect(live.attributes('data-tone')).toBe('red')
      expect(live.find('[data-testid="live-counts"]').text()).toBe('4 / 100 · 3 passed · 1 failed')
    })

    it('shows the first five runs of a suite and the rest on request', async () => {
      for (let i = 0; i < 7; i++) finishedRun(store, `r${i}`, 'Busy.Suite', `2026-09-01T10:${String(i).padStart(2, '0')}:00Z`)
      const page = render()
      await flushPromises()
      await expand(page)

      expect(page.findAll('[data-testid="run-card"]')).toHaveLength(5)
      expect(page.find('.bm-suite-older').text()).toContain('2 older runs not shown')
      await page.find('.bm-suite-show-all').trigger('click')
      expect(page.findAll('[data-testid="run-card"]')).toHaveLength(7)
    })

    it('gives a runner whose suite has not run yet a row with its Run button, saying the mode it will send', async () => {
      registered = [runner({ runnerId: 'r2', suite: 'Fresh.Specs', specs: 7 })]
      finishedRun(store, 'r1', 'Bank.Tests', '2026-09-01T11:50:00Z')
      registered.push(runner())
      const page = render()
      await flushPromises()

      expect(suiteNames(page)).toEqual(['Bank.Tests', 'Fresh.Specs'])
      const rows = page.findAll('[data-testid="suite-row"]')
      expect(rows[1]!.find('[data-testid="suite-latest"]').text()).toBe('no runs yet')
      expect(rows[1]!.find('[data-testid="suite-runner"]').text()).toContain('listening')
      expect(rows[1]!.find('[data-testid="run-button"]').text()).toBe('▶ Run all · cold')
      expect(rows[1]!.find('[data-testid="suite-toggle"]').attributes('disabled')).toBeDefined()
      expect(rows[0]!.find('[data-testid="run-button"]').exists()).toBe(true)
    })

    it('states what the console keeps', async () => {
      retention = { runs: 5, days: 14 }
      finishedRun(store, 'r1', 'Bank.Tests', '2026-09-01T11:50:00Z')
      const page = render()
      await flushPromises()
      expect(page.find('[data-testid="retention"]').text()).toBe('keeps 5 runs per suite · 14 days of archive')
    })

    it('says nothing about retention when the console does not answer', async () => {
      finishedRun(store, 'r1', 'Bank.Tests', '2026-09-01T11:50:00Z')
      const page = render()
      await flushPromises()
      expect(page.find('[data-testid="retention"]').exists()).toBe(false)
    })

    it('keeps the exports in the card menu rather than on the card', async () => {
      finishedRun(store, 'r1', 'One', '2026-09-01T11:50:00Z')
      const page = render()
      await flushPromises()
      await expand(page)
      const card = page.find('[data-testid="run-card"]')
      expect(card.text()).not.toContain('CTRF')
      expect(card.find('.bm-run-menu').exists()).toBe(true)
    })

    it('gives every icon button a name', async () => {
      finishedRun(store, 'r1', 'One', '2026-09-01T11:50:00Z')
      const page = render()
      await flushPromises()
      await expand(page)
      for (const button of page.findAll('button')) {
        expect(button.text().trim().length > 1 || !!button.attributes('aria-label'), button.html()).toBe(true)
      }
    })
  })

  describe('bulk eject (issue #197)', () => {
    it('offers a bulk eject only once there is more than one card to take', async () => {
      finishedRun(store, 'r1', 'One', '2026-09-01T11:50:00Z')
      const one = render()
      await flushPromises()
      expect(one.find('.bm-eject-all').exists()).toBe(false)

      finishedRun(store, 'r2', 'Two', '2026-09-01T11:51:00Z')
      const two = render()
      await flushPromises()
      expect(two.find('.bm-eject-all').text()).toContain('2')
    })

    it('confirms with the count and what survives, then clears the board', async () => {
      const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
      finishedRun(store, 'r1', 'One', '2026-09-01T11:50:00Z')
      finishedRun(store, 'r2', 'Two', '2026-09-01T11:51:00Z')

      const view = render()
      await flushPromises()
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
      await flushPromises()
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
      await flushPromises()
      await view.find('.bm-eject-all').trigger('click')
      await vi.waitFor(() => expect(store.allRuns).toHaveLength(1))

      expect(confirm.mock.calls[0][0]).toContain('1 still running — those stay.')
      expect(store.allRuns[0].runId).toBe('live')
    })

    it('ejects one run from its card', async () => {
      finishedRun(store, 'r1', 'One', '2026-09-01T11:50:00Z')
      const view = render()
      await flushPromises()
      await expand(view)
      await view.find('.bm-eject').trigger('click')
      await flushPromises()
      expect(deleted).toEqual(['/api/runs/r1'])
      expect(store.allRuns).toHaveLength(0)
    })
  })

  // stoat#76: runs are tagged by repository, and the board filters by it.
  describe('repositories', () => {
    function inRepository(runId: string, suite: string, path: string, minute: string) {
      store.handleRunStarted({ runId, suite, repository: path, branch: 'main', mode: 'in-process', startedAt: `2026-09-01T11:${minute}:00Z`, totalScenarios: 1 })
      store.handleRunFinished({ runId, exitCode: 0, passed: 1, failed: 0, passedOnRetry: 0, indeterminate: 0, finishedAt: `2026-09-01T11:${minute}:30Z` })
    }

    beforeEach(() => {
      inRepository('a', 'Stoat.Specs', '/code/stoat', '10')
      inRepository('b', 'Bobcat.Tests', '/code/bobcat', '20')
      inRepository('c', 'Scratch', '/tmp/scratch', '30')
      store.learnRepositories([
        { repository: '/code/stoat', repositorySlug: 'JasperFx/stoat' },
        { repository: '/code/bobcat', repositorySlug: 'JasperFx/bobcat' },
        { repository: '/tmp/scratch', repositorySlug: null },
      ])
    })

    it('names each suite by its repository on a real button, and keeps the local path for one nothing resolved', async () => {
      const page = render()
      await flushPromises()
      const pills = page.findAll('[data-testid="repository-pill"]')
      expect(pills.map((pill) => pill.text())).toEqual(['bobcat', 'stoat'])
      expect(pills.every((pill) => pill.element.tagName === 'BUTTON')).toBe(true)
      expect(pills[0]!.attributes('aria-label')).toBe('Filter the board to JasperFx/bobcat')
      expect(page.text()).toContain('/tmp/scratch')
    })

    it('two worktrees of one repository are one suite', async () => {
      inRepository('a2', 'Stoat.Specs', '/code/stoat-wt', '40')
      store.learnRepositories([{ repository: '/code/stoat-wt', repositorySlug: 'JasperFx/stoat' }])
      const page = render()
      await flushPromises()
      expect(suiteNames(page)).toEqual(['Stoat.Specs', 'Scratch', 'Bobcat.Tests'])
      expect(page.findAll('[data-testid="suite-row"]')[0]!.findAll('[data-testid="history-segment"]')).toHaveLength(2)
    })

    it('filters the board to one repository from the pill, or to the runs no repository claimed', async () => {
      const page = render()
      await flushPromises()

      await page.find('[data-testid="repository-pill"]').trigger('click')
      await flushPromises()
      expect(useBoardStore().repositoryFilter).toBe('JasperFx/bobcat')
      expect(suiteNames(page)).toEqual(['Bobcat.Tests'])

      useBoardStore().repositoryFilter = '(unattributed)'
      await flushPromises()
      expect(suiteNames(page)).toEqual(['Scratch'])
    })

    // stoat#83: "Eject all" on a filtered board means the runs on it.
    it('ejects only the filtered repository, and counts and labels it so', async () => {
      inRepository('a2', 'Stoat.Specs', '/code/stoat', '40')
      const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
      vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
        if (init?.method === 'DELETE') deleted.push(url)
        if (url === '/api/runners') return Promise.resolve({ ok: true, json: () => Promise.resolve([]) } as Response)
        return Promise.resolve({ ok: true, json: () => Promise.resolve({ count: 2, runIds: ['a', 'a2'] }) } as Response)
      })

      const page = render()
      await flushPromises()
      useBoardStore().repositoryFilter = 'JasperFx/stoat'
      await flushPromises()

      expect(page.find('.bm-eject-all').text()).toContain('(2)')
      await page.find('.bm-eject-all').trigger('click')
      await vi.waitFor(() => expect(deleted.length).toBe(1))

      expect(deleted[0]).toBe('/api/runs?repository=JasperFx%2Fstoat')
      expect(confirm.mock.calls[0][0]).toContain('Clear 2 runs')
      expect(confirm.mock.calls[0][1]).toBe('Eject all in JasperFx/stoat')
      expect(store.allRuns.map((run) => run.runId).sort()).toEqual(['b', 'c'])
    })

    it('ejects the unattributed runs alone when that is the filter', async () => {
      inRepository('c2', 'Scratch', '/tmp/scratch', '45')
      vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
      const page = render()
      await flushPromises()
      useBoardStore().repositoryFilter = '(unattributed)'
      await flushPromises()

      await page.find('.bm-eject-all').trigger('click')
      await vi.waitFor(() => expect(deleted.length).toBe(1))
      expect(deleted[0]).toBe('/api/runs?unattributed=true')
    })
  })
})
