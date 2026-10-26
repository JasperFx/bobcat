import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter, type Router } from 'vue-router'
import RunView from '../RunView.vue'
import SpecView from '../SpecView.vue'
import { useRunsStore } from '@/stores/runs-store'
import { useRunnersStore } from '@/stores/runners-store'
import { resetRunMode } from '@/composables/runMode'
import { placeCrumb } from '@/lib/crumbs'
import { parentAnchorOf, parentLabelOf } from '@/lib/runTree'

/**
 * bobcat#801 — the run page for suites of thousands: a supervised xUnit suite (no features, dotted
 * names) nests by namespace and class; nodes fold one at a time or all at once; a node can be zoomed
 * into, with a crumb per level back out; and the zoom and the status filter live in the URL.
 */
const RUN = '11111111-1111-1111-1111-111111111801'
const AT = '2026-10-26T10:42:00Z'

type Doc = { body: object }
const doc = (globalThis as unknown as { document: Doc }).document
type ElementCtor = { prototype: { scrollIntoView: (options?: unknown) => void } }
const ElementType = (globalThis as unknown as { Element: ElementCtor }).Element

let router: Router
let scrolled: string[] = []
let scrollSpy: ReturnType<typeof vi.spyOn>

function startRun(total: number) {
  const runs = useRunsStore()
  runs.handleRunStarted({
    runId: RUN,
    suite: 'Big.Tests',
    repository: '/code/big',
    branch: 'main',
    mode: 'supervised',
    startedAt: AT,
    totalScenarios: total,
    tag: null,
    session: null,
  })
  return runs
}

function finished(name: string, state: 'Passed' | 'Failed') {
  useRunsStore().handleTestFinished({ runId: RUN, uid: `u:${name}`, displayName: name, state, durationMs: 4, lane: 0, at: AT })
}

function started(name: string) {
  useRunsStore().handleTestStarted({ runId: RUN, uid: `u:${name}`, displayName: name, lane: 0, at: AT })
}

/** A small suite: App.Tests › Orders.OrderTests (1 failed) and Billing › InvoiceTests / Tax.TaxTests (one running). */
function small() {
  startRun(4)
  finished('App.Tests.Orders.OrderTests.places', 'Passed')
  finished('App.Tests.Orders.OrderTests.cancels', 'Failed')
  finished('App.Tests.Billing.InvoiceTests.bills', 'Passed')
  started('App.Tests.Billing.Tax.TaxTests.rounds')
}

/**
 * 1,200 tests three levels deep: Big.Tests › Area0..5 › Class0..9 › 20 tests each, with 3 failures
 * in Area2.Class3 and 2 in Area4.Class1.
 */
function large() {
  startRun(1200)
  for (let area = 0; area < 6; area++) {
    for (let cls = 0; cls < 10; cls++) {
      for (let test = 0; test < 20; test++) {
        const failed = (area === 2 && cls === 3 && test < 3) || (area === 4 && cls === 1 && test < 2)
        finished(`Big.Tests.Area${area}.Class${cls}.test_${test}`, failed ? 'Failed' : 'Passed')
      }
    }
  }
}

async function mountAt(path = `/runs/${RUN}`): Promise<VueWrapper> {
  router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/runs', name: 'runs', component: { template: '<div />' } },
      { path: '/runs/:runId', name: 'run', component: RunView, props: true },
      { path: '/runs/:runId/specs/:uid(.*)', name: 'spec', component: SpecView, props: true },
      { path: '/event-model', name: 'event-model', component: { template: '<div />' } },
    ],
  })
  useRunnersStore().runners = []
  useRunnersStore().builds = []
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

const railNames = (w: VueWrapper) => w.findAll('[data-testid="toc-feature"] .bm-toc-feature-node .bm-toc-name').map((n) => n.text())
const pageScenarios = (w: VueWrapper) => w.findAll('[data-testid="scenario"]').map((s) => s.attributes('data-uid'))
const railRow = (w: VueWrapper, name: string) =>
  w.findAll('[data-testid="toc-feature"]').find((row) => row.find('.bm-toc-feature-node .bm-toc-name').text() === name)!

beforeEach(() => {
  setActivePinia(createPinia())
  localStorage.clear()
  resetRunMode()
  scrolled = []
  scrollSpy = vi.spyOn(ElementType.prototype, 'scrollIntoView').mockImplementation(function (this: { id: string }) {
    scrolled.push(this.id)
  })
  vi.stubGlobal('fetch', () => Promise.resolve({ ok: true, json: () => Promise.resolve([]), text: () => Promise.resolve('') } as Response))
})

afterEach(() => {
  scrollSpy.mockRestore()
  vi.unstubAllGlobals()
})

describe('a supervised suite nests by namespace and class', () => {
  it('draws the levels on the rail and the page, with rollups at every level', async () => {
    small()
    const wrapper = await mountAt()

    expect(railNames(wrapper)).toEqual(['App.Tests', 'Orders.OrderTests', 'Billing', 'InvoiceTests', 'Tax.TaxTests'])
    expect(wrapper.findAll('[data-testid="toc-feature"]').map((r) => r.attributes('style'))).toEqual([
      '--bm-depth: 0;', '--bm-depth: 1;', '--bm-depth: 1;', '--bm-depth: 2;', '--bm-depth: 2;',
    ])
    expect(railRow(wrapper, 'Orders.OrderTests').attributes('data-status')).toBe('failed')
    expect(railRow(wrapper, 'Billing').attributes('data-status')).toBe('running')
    expect(wrapper.findAll('[data-testid="feature"]').map((s) => s.attributes('data-depth'))).toEqual(['0', '1', '1', '2', '2'])
    // The scenario is titled by its method; its class is the node above it.
    expect(wrapper.findAll('[data-testid="toc-scenario"] .bm-toc-name').map((n) => n.text())).toEqual(['places', 'cancels', 'bills', 'rounds'])
    wrapper.unmount()
  })
})

describe('the acceptance bar from bobcat#801', () => {
  it('1,200 tests three deep: one child suite’s failures in two clicks, the whole run again in one', async () => {
    large()
    const wrapper = await mountAt()

    // On arrival a big run folds what passed: only the failing branches are open.
    expect(railNames(wrapper).slice(0, 4)).toEqual(['Big.Tests', 'Area0', 'Area1', 'Area2'])
    expect(railRow(wrapper, 'Area0').attributes('data-folded')).toBe('true')
    expect(railRow(wrapper, 'Area2').attributes('data-folded')).toBeUndefined()
    expect(railRow(wrapper, 'Class3').attributes('data-folded')).toBeUndefined()
    expect(wrapper.findAll('[data-testid="toc-scenario"]')).toHaveLength(40) // Area2.Class3 and Area4.Class1 only

    // Click 1: the failed filter. Click 2: zoom into Area4.
    await wrapper.find('[data-testid="filter-failed"]').trigger('click')
    await railRow(wrapper, 'Area4').find('[data-testid="toc-zoom"]').trigger('click')
    await flushPromises()

    expect(router.currentRoute.value.query).toEqual({ status: 'failed', in: 'suite:Big.Tests.Area4' })
    expect(pageScenarios(wrapper)).toEqual(['u:Big.Tests.Area4.Class1.test_0', 'u:Big.Tests.Area4.Class1.test_1'])
    expect(wrapper.find('[data-testid="tile-failed"] .bm-tile-count').text()).toBe('2')
    expect(wrapper.find('[data-testid="tile-passed"] .bm-tile-count').text()).toBe('198')
    expect(wrapper.findAll('[data-testid="crumb-zoom"]').map((c) => c.text())).toEqual(['Big.Tests', 'Area4'])

    // One click back to the whole run — the filter rides along.
    await wrapper.find('[data-testid="crumb-run"]').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({ status: 'failed' })
    expect(pageScenarios(wrapper)).toHaveLength(5)
    expect(wrapper.find('[data-testid="tile-failed"] .bm-tile-count').text()).toBe('5')
    wrapper.unmount()
  })
})

describe('zooming', () => {
  it('the page’s section zoom and a crumb per level, each level leading back to itself', async () => {
    small()
    const wrapper = await mountAt()

    const tax = wrapper.findAll('[data-testid="feature"]').find((s) => s.find('.bm-feature-name').text() === 'Tax.TaxTests')!
    await tax.find('[data-testid="section-zoom"]').trigger('click')
    await flushPromises()

    const crumbs = wrapper.findAll('[data-testid="crumb-zoom"]')
    expect(crumbs.map((c) => [c.text(), c.element.tagName])).toEqual([
      ['App.Tests', 'A'],
      ['Billing', 'A'],
      ['Tax.TaxTests', 'SPAN'],
    ])
    expect(wrapper.find('[data-testid="crumb-run"]').element.tagName).toBe('A')
    expect(pageScenarios(wrapper)).toEqual(['u:App.Tests.Billing.Tax.TaxTests.rounds'])
    // The zoomed node offers no zoom into itself; the rail offers the whole run back.
    expect(wrapper.find('[data-testid="section-zoom"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="toc-unzoom"]').exists()).toBe(true)

    await crumbs[1]!.trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({ in: 'suite:App.Tests.Billing' })
    expect(pageScenarios(wrapper)).toEqual(['u:App.Tests.Billing.InvoiceTests.bills', 'u:App.Tests.Billing.Tax.TaxTests.rounds'])

    await wrapper.find('[data-testid="toc-unzoom"]').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({})
    expect(pageScenarios(wrapper)).toHaveLength(4)
    wrapper.unmount()
  })

  it('a link that carries ?in= and ?status= arrives zoomed and filtered', async () => {
    small()
    const wrapper = await mountAt(`/runs/${RUN}?in=suite:App.Tests.Orders.OrderTests&status=failed`)

    expect(pageScenarios(wrapper)).toEqual(['u:App.Tests.Orders.OrderTests.cancels'])
    expect(wrapper.find('[data-testid="filter-failed"]').attributes('data-active')).toBe('true')
    wrapper.unmount()
  })

  it('a stale ?in= shows the whole run rather than nothing', async () => {
    small()
    const wrapper = await mountAt(`/runs/${RUN}?in=suite:Gone`)
    expect(pageScenarios(wrapper)).toHaveLength(4)
    expect(wrapper.find('[data-testid="crumb-zoom"]').exists()).toBe(false)
    wrapper.unmount()
  })
})

describe('folding', () => {
  it('fold-all leaves the top level; unfold-all opens every level; one node folds on its own', async () => {
    small()
    const wrapper = await mountAt()

    await wrapper.find('[data-testid="collapse-all"]').trigger('click')
    expect(railNames(wrapper)).toEqual(['App.Tests'])
    expect(wrapper.findAll('[data-testid="toc-scenario"]')).toHaveLength(0)
    expect(wrapper.findAll('[data-testid="feature"]')).toHaveLength(1)
    expect(pageScenarios(wrapper)).toHaveLength(0)
    // A folded node still says what is in it.
    expect(railRow(wrapper, 'App.Tests').findAll('[data-testid="toc-rollup"] span').map((c) => c.text())).toEqual(['1', '1', '2'])

    await wrapper.find('[data-testid="expand-all"]').trigger('click')
    expect(railNames(wrapper)).toHaveLength(5)
    expect(pageScenarios(wrapper)).toHaveLength(4)

    // The rail's twisty and the page's fold are one state.
    await railRow(wrapper, 'Billing').find('[data-testid="toc-toggle"]').trigger('click')
    expect(railNames(wrapper)).toEqual(['App.Tests', 'Orders.OrderTests', 'Billing'])
    const billing = wrapper.findAll('[data-testid="feature"]').find((s) => s.find('.bm-feature-name').text() === 'Billing')!
    expect(billing.attributes('data-folded')).toBe('true')
    await billing.find('[data-testid="section-toggle"]').trigger('click')
    expect(railNames(wrapper)).toHaveLength(5)
    wrapper.unmount()
  })

  it('zooming into a passed node on a big run opens it, its own passed children still folded', async () => {
    large()
    const wrapper = await mountAt()
    expect(railRow(wrapper, 'Area0').attributes('data-folded')).toBe('true')

    await railRow(wrapper, 'Area0').find('[data-testid="toc-zoom"]').trigger('click')
    await flushPromises()

    expect(railRow(wrapper, 'Area0').attributes('data-folded')).toBeUndefined()
    expect(railNames(wrapper)).toEqual(['Area0', ...Array.from({ length: 10 }, (_, i) => `Class${i}`)])
    expect(railRow(wrapper, 'Class0').attributes('data-folded')).toBe('true')
    wrapper.unmount()
  })

  it('a filter shows everything it matches unfolded, even on a big run', async () => {
    large()
    const wrapper = await mountAt()
    await wrapper.find('[data-testid="filter-passed"]').trigger('click')
    expect(railRow(wrapper, 'Area0').attributes('data-folded')).toBeUndefined()
    wrapper.unmount()
  })
})

describe('"Go to running" and links reach through folds and zooms', () => {
  it('zooms back out and unfolds the way to the running test', async () => {
    small()
    const wrapper = await mountAt()
    // Fold everything, open the top, then zoom into the branch the running test is NOT in.
    await wrapper.find('[data-testid="collapse-all"]').trigger('click')
    await railRow(wrapper, 'App.Tests').find('[data-testid="toc-toggle"]').trigger('click')
    expect(railRow(wrapper, 'Billing').attributes('data-folded')).toBe('true')
    await railRow(wrapper, 'Orders.OrderTests').find('[data-testid="toc-zoom"]').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({ in: 'suite:App.Tests.Orders.OrderTests' })

    await wrapper.find('[data-testid="go-to-running"]').trigger('click')
    await flushPromises()

    expect(router.currentRoute.value.query).toEqual({})
    expect(railRow(wrapper, 'Billing').attributes('data-folded')).toBeUndefined()
    expect(railRow(wrapper, 'Tax.TaxTests').attributes('data-folded')).toBeUndefined()
    expect(scrolled).toEqual(['spec:u:App.Tests.Billing.Tax.TaxTests.rounds'])
    wrapper.unmount()
  })

  it('a link into a folded section unfolds the nodes above it', async () => {
    large()
    const wrapper = await mountAt(`/runs/${RUN}#suite:Big.Tests.Area0.Class5`)
    await flushPromises()
    expect(railRow(wrapper, 'Area0').attributes('data-folded')).toBeUndefined()
    expect(scrolled).toEqual(['suite:Big.Tests.Area0.Class5'])
    wrapper.unmount()
  })

  it('the spec page’s crumb lands on the class a forwarded test sits in', () => {
    const scenario = { feature: '', scenario: 'App.Tests.Billing.Tax.TaxTests.rounds(amount: 1.5)' }
    const crumb = placeCrumb(RUN, parentAnchorOf(scenario), parentLabelOf(scenario), false)
    expect(crumb.label).toBe('App.Tests.Billing.Tax.TaxTests')
    expect(crumb.to).toEqual({ path: `/runs/${RUN}`, hash: '#suite:App.Tests.Billing.Tax.TaxTests' })
  })
})
