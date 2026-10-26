import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter, type Router } from 'vue-router'
import RunView from '../RunView.vue'
import SpecView from '../SpecView.vue'
import { useRunsStore } from '@/stores/runs-store'
import { useRunnersStore } from '@/stores/runners-store'
import { resetRunMode } from '@/composables/runMode'
import type { RunnerView } from '@/lib/runners'

/**
 * bobcat#787 — the run page a reader can orient in: its identity in the header, summary tiles, a
 * table-of-contents rail with a status on every node, "Go to running" and follow, and passed
 * scenarios folded while failed and running ones stay open. Mounted into the document, because the
 * rail's jump scrolls a real element into view.
 */
const RUN = '11111111-1111-1111-1111-111111111787'

// The vitest tsconfig has no DOM lib; the document is reached by shape.
type Doc = { body: object; getElementById(id: string): { scrollIntoView?: unknown } | null }
const doc = (globalThis as unknown as { document: Doc }).document
type ElementCtor = { prototype: { scrollIntoView: (options?: unknown) => void } }
const ElementType = (globalThis as unknown as { Element: ElementCtor }).Element

function seed(options: { finish?: boolean } = {}) {
  const runs = useRunsStore()
  runs.handleRunStarted({
    runId: RUN,
    suite: 'Wallets',
    repository: '/code/wallets',
    branch: 'main',
    mode: 'supervised',
    startedAt: '2026-10-26T10:42:00Z',
    totalScenarios: 4,
    tag: 'build-512',
    session: 'session-7',
  })

  const start = (uid: string, at: string) => {
    const slash = uid.indexOf('/')
    runs.handleScenarioStarted({ runId: RUN, uid, feature: uid.slice(0, slash), scenario: uid.slice(slash + 1), attempt: 1, at })
  }
  const finish = (uid: string, outcome: string, at: string) =>
    runs.handleScenarioFinished({ runId: RUN, uid, outcome, attempts: 1, durationMs: 12, errorMessage: null, at })

  start('Wallet/credit', '2026-10-26T10:42:01Z')
  finish('Wallet/credit', 'CleanPass', '2026-10-26T10:42:02Z')
  start('Wallet/debit', '2026-10-26T10:42:02Z')
  runs.handleStepStarted({ runId: RUN, uid: 'Wallet/debit', stepId: 's1', kind: 'Then', text: 'the balance is 5' })
  runs.handleStepFinished({
    runId: RUN,
    uid: 'Wallet/debit',
    stepId: 's1',
    status: 'failed',
    durationMs: 1,
    errorMessage: "expected '5', got '0'",
    cells: [{ name: 'balance', status: 'failed', expected: '5', actual: '0', note: null, rowIndex: -1, value: null, comparison: null }],
  })
  finish('Wallet/debit', 'Failed', '2026-10-26T10:42:03Z')
  start('Orders/place', '2026-10-26T10:42:03Z')
  runs.handleStepStarted({ runId: RUN, uid: 'Orders/place', stepId: 's1', kind: 'Given', text: 'a cart' })

  if (options.finish) {
    finish('Orders/place', 'CleanPass', '2026-10-26T10:42:04Z')
    runs.handleRunFinished({ runId: RUN, exitCode: 1, passed: 2, failed: 1, passedOnRetry: 0, indeterminate: 0, finishedAt: '2026-10-26T10:42:05Z' })
  }
  return runs
}

/** The console's pushed runner list, as a page finds it: both halves loaded, so nothing fetches over it. */
function pushed(...runners: RunnerView[]): void {
  const store = useRunnersStore()
  store.runners = runners
  store.builds = []
}

function runner(overrides: Partial<RunnerView> = {}): RunnerView {
  return {
    runnerId: 'r1', repository: '/code/wallets', repositorySlug: 'JasperFx/wallets', branch: 'main', suite: 'Wallets',
    lane: 'projected', modes: ['cold', 'warm'], specs: 4, connected: true, status: null, statusDetail: null,
    registeredAt: '', lastSeenAt: '', commands: [], builtAt: null, ...overrides,
  }
}

let router: Router
let scrolled: string[]
let scrollSpy: ReturnType<typeof vi.spyOn>

async function mountRun(path = `/runs/${RUN}`): Promise<VueWrapper> {
  router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/runs', name: 'runs', component: { template: '<div />' } },
      { path: '/runs/:runId', name: 'run', component: RunView, props: true },
      { path: '/runs/:runId/specs/:uid(.*)', name: 'spec', component: SpecView, props: true },
      { path: '/event-model', name: 'event-model', component: { template: '<div />' } },
    ],
  })
  await router.push(path)
  await router.isReady()
  const wrapper = mount(RunView, {
    props: { runId: RUN },
    attachTo: doc.body as never,
    global: { plugins: [router], stubs: { teleport: true } },
  })
  await flushPromises()
  return wrapper
}

beforeEach(() => {
  setActivePinia(createPinia())
  localStorage.clear()
  resetRunMode()
  scrolled = []
  // Every scroll records the id of the element asked to come into view.
  scrollSpy = vi.spyOn(ElementType.prototype, 'scrollIntoView').mockImplementation(function (this: { id: string }) {
    scrolled.push(this.id)
  })
  vi.stubGlobal('fetch', () => Promise.resolve({ ok: true, json: () => Promise.resolve([]), text: () => Promise.resolve('') } as Response))
})

afterEach(() => {
  scrollSpy.mockRestore()
  vi.unstubAllGlobals()
})

describe('the run page’s identity and summary', () => {
  it('leads with a breadcrumb trail whose run segment carries the run’s identity', async () => {
    seed()
    const wrapper = await mountRun()

    expect(wrapper.find('[data-testid="crumb-runs"]').attributes('href')).toBe('/runs')
    expect(wrapper.find('[data-testid="crumb-suite"]').text()).toBe('Wallets')
    const run = wrapper.find('[data-testid="crumb-run"]')
    expect(run.text()).toContain('build-512')
    expect(run.text()).toContain('main')
    expect(run.element.tagName).toBe('SPAN') // the current page is not a link
    wrapper.unmount()
  })

  it('says when the run started, how long it has been going, its tag, checkout, mode and session', async () => {
    seed()
    const wrapper = await mountRun()

    const identity = wrapper.find('[data-testid="run-identity"]')
    expect(wrapper.find('[data-testid="run-tag"]').text()).toBe('build-512')
    expect(wrapper.find('[data-testid="run-session"]').text()).toBe('session-7')
    expect(wrapper.find('[data-testid="run-mode"]').text()).toBe('supervised')
    expect(wrapper.find('[data-testid="run-clock"]').text()).toMatch(/^running for /)
    // The checkout path is the tooltip, not the line.
    expect(wrapper.find('[data-testid="run-checkout"]').attributes('title')).toBe('/code/wallets')
    expect(identity.text()).toContain('@ main')
    wrapper.unmount()
  })

  it('a finished run says how long it took, and a commanded one says its command and mode', async () => {
    seed({ finish: true })
    const command = { commandId: 'c1', kind: 'run', specs: ['Wallet/credit', 'Wallet/debit'], mode: 'warm', state: 'accepted', reason: null, requestedBy: 'bobcat-ui', requestedAt: '', acknowledgedAt: null, runId: RUN }
    pushed(runner({ commands: [command] }))
    const wrapper = await mountRun()

    expect(wrapper.find('[data-testid="run-clock"]').text()).toBe('took 5.0s')
    expect(wrapper.find('[data-testid="run-mode"]').text()).toBe('supervised · Run 2 in feature Wallet · warm · from bobcat-ui')
    wrapper.unmount()
  })

  it('counts the run in tiles, and the bar goes red at the first failure', async () => {
    seed()
    const wrapper = await mountRun()

    expect(wrapper.find('[data-testid="tile-passed"] .bm-tile-count').text()).toBe('1')
    expect(wrapper.find('[data-testid="tile-failed"] .bm-tile-count').text()).toBe('1')
    expect(wrapper.find('[data-testid="tile-running"] .bm-tile-count').text()).toBe('1')
    expect(wrapper.find('[data-testid="tile-pending"] .bm-tile-count').text()).toBe('1') // 4 declared, 3 heard of
    expect(wrapper.find('[data-testid="tile-retried"]').exists()).toBe(false)
    // bobcat#786's bar, the board's own: red at the first failure.
    expect(wrapper.find('[data-testid="live-progress"]').attributes('data-tone')).toBe('red')
    wrapper.unmount()
  })
})

describe('the table-of-contents rail', () => {
  it('is the suite tree with a status on every node and rollup counts per feature', async () => {
    seed()
    const wrapper = await mountRun()

    expect(wrapper.find('[data-testid="toc-suite"]').attributes('data-status')).toBe('running')
    const features = wrapper.findAll('[data-testid="toc-feature"]')
    expect(features.map((f) => f.attributes('data-status'))).toEqual(['failed', 'running'])
    const rollup = features[0]!.findAll('[data-testid="toc-rollup"] span').map((c) => [c.attributes('data-status'), c.text()])
    expect(rollup).toEqual([
      ['failed', '1'],
      ['passed', '1'],
    ])
    const scenarios = wrapper.findAll('[data-testid="toc-scenario"]')
    expect(scenarios.map((s) => [s.find('.bm-toc-name').text(), s.attributes('data-status')])).toEqual([
      ['credit', 'passed'],
      ['debit', 'failed'],
      ['place', 'running'],
    ])
    expect(scenarios[2]!.attributes('data-running')).toBe('true')
    wrapper.unmount()
  })

  it('filters the rail and the page by status, and a tile is the same filter', async () => {
    seed()
    const wrapper = await mountRun()

    await wrapper.find('[data-testid="filter-failed"]').trigger('click')
    expect(wrapper.findAll('[data-testid="toc-scenario"]').map((s) => s.find('.bm-toc-name').text())).toEqual(['debit'])
    expect(wrapper.findAll('[data-testid="scenario"]').map((s) => s.attributes('data-uid'))).toEqual(['Wallet/debit'])
    expect(wrapper.find('[data-testid="tile-failed"]').attributes('data-active')).toBe('true')

    // The tile toggles it off again.
    await wrapper.find('[data-testid="tile-failed"]').trigger('click')
    expect(wrapper.findAll('[data-testid="scenario"]')).toHaveLength(3)
    wrapper.unmount()
  })

  it('clicking a node scrolls that scenario — or that feature’s section — into view', async () => {
    seed()
    const wrapper = await mountRun()

    await wrapper.findAll('[data-testid="toc-scenario"] button')[1]!.trigger('click')
    await flushPromises()
    expect(scrolled).toEqual(['spec:Wallet/debit'])

    await wrapper.findAll('[data-testid="toc-feature"] > button')[1]!.trigger('click')
    await flushPromises()
    expect(scrolled).toEqual(['spec:Wallet/debit', 'feature:Orders'])
    wrapper.unmount()
  })

  it('"Go to running" scrolls the running scenario into view, lifting a filter that hides it', async () => {
    seed()
    const wrapper = await mountRun()

    await wrapper.find('[data-testid="filter-failed"]').trigger('click')
    await wrapper.find('[data-testid="run-toc"] [data-testid="go-to-running"]').trigger('click')
    await flushPromises()

    expect(scrolled).toEqual(['spec:Orders/place'])
    expect(wrapper.findAll('[data-testid="scenario"]')).toHaveLength(3)
    wrapper.unmount()
  })

  it('"Go to running" has nothing to go to on a finished run, which has no live bar either', async () => {
    seed({ finish: true })
    const wrapper = await mountRun()
    expect(wrapper.find('[data-testid="run-toc"] [data-testid="go-to-running"]').attributes('disabled')).toBeDefined()
    expect(wrapper.find('[data-testid="live-progress"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('arriving at #running — the board\'s "Go to running" (bobcat#786) — scrolls to the running scenario', async () => {
    seed()
    const wrapper = await mountRun(`/runs/${RUN}#running`)
    await flushPromises()
    expect(scrolled).toEqual(['spec:Orders/place'])
    wrapper.unmount()
  })

  it('follow keeps the running scenario in view as the run advances', async () => {
    const runs = seed()
    const wrapper = await mountRun()

    // Nothing moves until asked.
    expect(scrolled).toEqual([])
    await wrapper.find('[data-testid="follow"]').trigger('click')
    await flushPromises()
    expect(scrolled).toEqual(['spec:Orders/place'])

    // The running scenario finishes and another starts: the page follows.
    runs.handleScenarioFinished({ runId: RUN, uid: 'Orders/place', outcome: 'CleanPass', attempts: 1, durationMs: 3, errorMessage: null, at: '2026-10-26T10:42:04Z' })
    runs.handleScenarioStarted({ runId: RUN, uid: 'Orders/cancel', feature: 'Orders', scenario: 'cancel', attempt: 1, at: '2026-10-26T10:42:05Z' })
    await flushPromises()
    expect(scrolled).toEqual(['spec:Orders/place', 'spec:Orders/cancel'])

    // And as that one grows a step, so the step in flight stays on screen.
    runs.handleStepStarted({ runId: RUN, uid: 'Orders/cancel', stepId: 's1', kind: 'Given', text: 'an order' })
    await flushPromises()
    expect(scrolled).toEqual(['spec:Orders/place', 'spec:Orders/cancel', 'spec:Orders/cancel'])

    // Off again: a new scenario moves nothing.
    await wrapper.find('[data-testid="follow"]').trigger('click')
    runs.handleScenarioStarted({ runId: RUN, uid: 'Orders/refund', feature: 'Orders', scenario: 'refund', attempt: 1, at: '2026-10-26T10:42:06Z' })
    await flushPromises()
    expect(scrolled).toHaveLength(3)
    wrapper.unmount()
  })

  it('a link into a feature lands on its section once the run is on the page', async () => {
    seed()
    const wrapper = await mountRun(`/runs/${RUN}#feature:Orders`)
    await flushPromises()
    expect(scrolled).toEqual(['feature:Orders'])
    wrapper.unmount()
  })
})

describe('what starts open', () => {
  it('folds a passed scenario to its line and opens a failed or running one on its specification', async () => {
    seed()
    const wrapper = await mountRun()

    const byUid = Object.fromEntries(wrapper.findAll('[data-testid="scenario"]').map((s) => [s.attributes('data-uid'), s]))
    expect((byUid['Wallet/credit']!.element as { open?: boolean }).open).toBe(false)
    expect(byUid['Wallet/credit']!.find('[data-testid="specification"]').exists()).toBe(false)
    expect((byUid['Wallet/debit']!.element as { open?: boolean }).open).toBe(true)
    expect(byUid['Wallet/debit']!.find('[data-testid="cell"]').text()).toContain("expected '5', got '0'")
    expect((byUid['Orders/place']!.element as { open?: boolean }).open).toBe(true)

    // Every scenario still links to its page, and a failed one offers its re-run.
    expect(byUid['Wallet/credit']!.find('[data-testid="spec-link"]').attributes('href')).toContain('/specs/Wallet%2Fcredit')
    expect(byUid['Wallet/debit']!.find('[data-testid="run-button"]').exists()).toBe(true)
    expect(byUid['Wallet/credit']!.find('[data-testid="run-button"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('a scenario the command named but the run has not reached is on the page as “no result yet”', async () => {
    seed()
    const command = { commandId: 'c1', kind: 'run', specs: ['Wallet/credit', 'Wallet/debit', 'Orders/place', 'Orders/cancel'], mode: 'cold', state: 'accepted', reason: null, requestedBy: 'bobcat-ui', requestedAt: '', acknowledgedAt: null, runId: RUN }
    pushed(runner({ commands: [command] }))
    const wrapper = await mountRun()

    expect(wrapper.find('[data-testid="pending-scenario"]').text()).toContain('cancel')
    expect(wrapper.findAll('[data-testid="toc-scenario"]').map((s) => s.attributes('data-status'))).toEqual(['passed', 'failed', 'running', 'pending'])
    wrapper.unmount()
  })

  it('the run buttons say the mode they will send', async () => {
    seed()
    const wrapper = await mountRun()
    const labels = wrapper.findAll('[data-testid="run-button"]').map((b) => b.text())
    expect(labels).toContain('▶ Re-run failed (1) · cold')
    expect(labels).toContain('▶ Run again (3) · cold')
    wrapper.unmount()
  })
})

describe('a broken build on the run page (bobcat#795)', () => {
  it('says the suite\'s build is broken, with the compiler\'s errors, when the report names this suite and checkout', async () => {
    seed()
    pushed()
    useRunnersStore().builds = [
      { suite: 'Wallets', repository: '/code/wallets', branch: 'main', state: 'build-failed', detail: 'Wallet.cs(9,4): error CS0103: The name \'notAThing\' does not exist', at: '2026-10-26T10:43:00Z' },
    ]
    const wrapper = await mountRun()
    const alert = wrapper.find('[data-testid="run-build"]')
    expect(alert.exists()).toBe(true)
    expect(alert.text()).toContain('notAThing')
    wrapper.unmount()
  })

  it('stays quiet for a build of another suite, which once read as this one because watch named it by project file', async () => {
    seed()
    pushed()
    useRunnersStore().builds = [
      { suite: 'Tests', repository: '/code/wallets', branch: 'main', state: 'build-failed', detail: 'error CS0103', at: '2026-10-26T10:43:00Z' },
    ]
    const wrapper = await mountRun()
    expect(wrapper.find('[data-testid="run-build"]').exists()).toBe(false)
    wrapper.unmount()
  })
})
