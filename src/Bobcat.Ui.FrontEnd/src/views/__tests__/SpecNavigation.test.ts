import { beforeEach, describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter, type Router } from 'vue-router'
import type { EventModelDescriptor } from '@jasperfx/event-model-vue'
import EventModelPage from '../EventModelPage.vue'
import RunView from '../RunView.vue'
import SpecView from '../SpecView.vue'
import { useEventModelStore } from '@/stores/event-model-store'
import { useRunsStore } from '@/stores/runs-store'
import { specHref } from '@/lib/specLinks'

/**
 * stoat#61 — one rendered specification, reachable both ways between a slice and its specs. The
 * join is the spec identity `{Feature}/{Scenario}` and nothing else, and it carries a slash, so
 * every case goes through a real (memory) router with the app's real route shapes.
 */
const RUN = '11111111-1111-1111-1111-111111111061'
const UID = 'Wallet/Crediting a wallet'

function descriptor(): EventModelDescriptor {
  return {
    name: 'Wallets',
    slices: [
      {
        name: 'CreditWallet',
        pattern: 'Command',
        elements: [],
        edges: [],
        specifications: [{ identity: UID, resolvedTypes: [] }],
      },
    ],
  }
}

function seed() {
  const model = useEventModelStore()
  model.descriptor = descriptor()
  model.status = 'loaded'

  const runs = useRunsStore()
  runs.handleRunStarted({
    runId: RUN,
    suite: 'Wallets',
    repository: '/repo',
    branch: 'main',
    mode: 'in-process',
    startedAt: '2026-09-29T10:00:00Z',
    totalScenarios: 1,
  })
  runs.handleScenarioStarted({
    runId: RUN,
    uid: UID,
    feature: 'Wallet',
    scenario: 'Crediting a wallet',
    attempt: 1,
    at: '2026-09-29T10:00:01Z',
  })
  runs.handleStepStarted({ runId: RUN, uid: UID, stepId: 's1', kind: 'When', text: 'CreditWallet is received' })
  runs.handleStepFinished({
    runId: RUN,
    uid: UID,
    stepId: 's1',
    status: 'failed',
    durationMs: 3,
    errorMessage: "Balance: expected '10', got '0'",
    cells: [{ name: 'Balance', status: 'failed', expected: '10', actual: '0', note: null, rowIndex: -1, value: null, comparison: null }],
  })
  runs.handleScenarioFinished({
    runId: RUN,
    uid: UID,
    outcome: 'Failed',
    attempts: 1,
    durationMs: 5,
    errorMessage: null,
    at: '2026-09-29T10:00:02Z',
  })
}

let router: Router

async function mountAt(path: string) {
  router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/runs/:runId', name: 'run', component: RunView, props: true },
      { path: '/runs/:runId/specs/:uid(.*)', name: 'spec', component: SpecView, props: true },
      { path: '/event-model', name: 'event-model', component: EventModelPage },
    ],
  })
  await router.push(path)
  await router.isReady()

  const component = router.currentRoute.value.matched[0]!.components!.default
  const wrapper = mount(component as never, {
    props: router.currentRoute.value.params as never,
    global: { plugins: [router], stubs: { teleport: true, ElSelect: true, ElOption: true } },
  })
  await flushPromises()
  return wrapper
}

beforeEach(() => setActivePinia(createPinia()))

describe('specification → its page and its slice', () => {
  it('a spec identity with a slash survives the route as one param', async () => {
    seed()
    await mountAt(specHref(RUN, UID))
    expect(router.currentRoute.value.params.uid).toBe(UID)
  })

  it('the spec page renders the specification through the shared renderer, cells and all', async () => {
    seed()
    const wrapper = await mountAt(specHref(RUN, UID))

    expect(wrapper.find('[data-testid="specification"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="cell"]').text()).toContain("Balance: expected '10', got '0'")
    expect(wrapper.find('[data-testid="spec-verdict"]').text()).toBe('Failed')
  })

  it('the spec page links to the slice it is evidence for, focused, from this run', async () => {
    seed()
    const wrapper = await mountAt(specHref(RUN, UID))

    const href = wrapper.find('[data-testid="slice-link"]').attributes('href')!
    const target = router.resolve(href)
    expect(target.path).toBe('/event-model')
    expect(target.query).toEqual({ focus: 'slice:CreditWallet', spec: UID, run: RUN })
  })

  // bobcat#787: the way back is a trail, and its run segment says which run.
  it('the spec page leads with breadcrumbs: Runs › suite › run (identity) › feature › scenario', async () => {
    seed()
    const wrapper = await mountAt(specHref(RUN, UID))

    expect(router.resolve(wrapper.find('[data-testid="crumb-runs"]').attributes('href')!).path).toBe('/runs')
    expect(wrapper.find('[data-testid="crumb-suite"]').text()).toBe('Wallets')
    const run = wrapper.find('[data-testid="crumb-run"]')
    expect(run.text()).toContain('main')
    expect(router.resolve(run.attributes('href')!).path).toBe(`/runs/${RUN}`)
    const feature = router.resolve(wrapper.find('[data-testid="crumb-feature"]').attributes('href')!)
    expect(feature.path).toBe(`/runs/${RUN}`)
    expect(decodeURIComponent(feature.hash)).toBe('#feature:Wallet')
    expect(wrapper.find('[data-testid="crumb-scenario"]').text()).toBe('Crediting a wallet')
    expect(wrapper.find('[data-testid="crumb-scenario"]').attributes('href')).toBeUndefined()
  })

  it('a spec no slice binds has no slice link — design-time only is an answer, not an error', async () => {
    seed()
    useEventModelStore().descriptor = { name: 'Empty', slices: [] }
    const wrapper = await mountAt(specHref(RUN, UID))
    expect(wrapper.find('[data-testid="slice-link"]').exists()).toBe(false)
  })

  it('the run page links each scenario to its spec page and its slice', async () => {
    seed()
    const wrapper = await mountAt(`/runs/${RUN}`)

    const spec = router.resolve(wrapper.find('[data-testid="spec-link"]').attributes('href')!)
    expect(spec.name).toBe('spec')
    expect(spec.params.uid).toBe(UID)

    const slice = router.resolve(wrapper.find('[data-testid="slice-link"]').attributes('href')!)
    expect(slice.query.focus).toBe('slice:CreditWallet')
  })
})

describe('slice → its specifications', () => {
  it('arriving with ?spec= opens the drill-down on the slice that binds it, and marks the spec', async () => {
    seed()
    const wrapper = await mountAt(`/event-model?focus=slice:CreditWallet&spec=${encodeURIComponent(UID)}&run=${RUN}`)

    const drill = wrapper.find('[data-testid="slice-drilldown"]')
    expect(drill.exists()).toBe(true)
    expect(wrapper.find(`[data-spec="${UID}"]`).attributes('data-arrived')).toBe('true')
    // The whole rendered specification, not just its identity.
    expect(drill.find('[data-testid="specification"]').exists()).toBe(true)
    expect(drill.find('[data-testid="cell"]').exists()).toBe(true)
  })

  it('each bound spec links to its rendered page in the evidence run', async () => {
    seed()
    const wrapper = await mountAt(`/event-model?spec=${encodeURIComponent(UID)}`)

    const target = router.resolve(wrapper.find('[data-testid="open-spec"]').attributes('href')!)
    expect(target.name).toBe('spec')
    expect(target.params).toEqual({ runId: RUN, uid: UID })
  })
})
