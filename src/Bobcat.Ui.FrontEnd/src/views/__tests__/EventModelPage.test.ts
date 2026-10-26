import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter, type Router } from 'vue-router'
import type { EventModelDescriptor } from '@jasperfx/event-model-vue'
import EventModelPage from '../EventModelPage.vue'
import { useEventModelStore } from '@/stores/event-model-store'
import { useRunsStore } from '@/stores/runs-store'

/**
 * The page around the shared renderer (issue #108): descriptor in, slice colouring from run
 * evidence, and the drill-down drawer with the bound scenarios' step results. The renderer's
 * own layout is pinned in @jasperfx/event-model-vue's specs — here we test only the page's
 * wiring of it.
 */
const RUN = '11111111-1111-1111-1111-111111111108'

function descriptor(): EventModelDescriptor {
  return {
    name: 'Wallets',
    slices: [
      {
        name: 'CreditWallet',
        pattern: 'Command',
        elements: [
          {
            id: 'CreditWallet/Command/Wallets.CreditWallet',
            kind: 'Command',
            lane: 'Command',
            label: 'CreditWallet',
            type: { name: 'CreditWallet', fullName: 'Wallets.CreditWallet' },
          },
        ],
        edges: [],
        specifications: [{ identity: 'Wallet/Crediting a wallet', resolvedTypes: [] }],
      },
    ],
  }
}

function seedStores() {
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
    startedAt: '2026-08-24T10:00:00Z',
    totalScenarios: 1,
    tag: null,
  })
  runs.handleScenarioStarted({
    runId: RUN,
    uid: 'Wallet/Crediting a wallet',
    feature: 'Wallet',
    scenario: 'Crediting a wallet',
    attempt: 1,
    at: '2026-08-24T10:00:01Z',
  })
  runs.handleStepStarted({
    runId: RUN,
    uid: 'Wallet/Crediting a wallet',
    stepId: 's1',
    kind: 'When',
    text: 'CreditWallet is received',
  })
  runs.handleStepFinished({
    runId: RUN,
    uid: 'Wallet/Crediting a wallet',
    stepId: 's1',
    status: 'success',
    durationMs: 12,
    errorMessage: null,
  })
  runs.handleScenarioFinished({
    runId: RUN,
    uid: 'Wallet/Crediting a wallet',
    outcome: 'CleanPass',
    attempts: 1,
    durationMs: 40,
    errorMessage: null,
    touchedTypes: [
      { name: 'CreditWallet', fullName: 'Wallets.CreditWallet', assemblyName: 'W' },
      { name: 'WalletSummary', fullName: 'Wallets.WalletSummary', assemblyName: 'W' },
    ],
    at: '2026-08-24T10:00:02Z',
  })
}

// el-drawer teleports its overlay to the body; stubbing the teleport keeps the drawer's
// content inside the wrapper, so assertions never need DOM globals (the vitest tsconfig
// deliberately clears "lib"). ElSelect's popper cannot survive that stubbing (it recursively
// re-renders), and the run picker is not what these cases assert — so it is stubbed out.
//
// A real (memory) router, not a stub: #296 has the page mirror the canvas viewport into the route
// query, and a stubbed `$route` could not tell us whether a link actually round-trips.
let router: Router

async function mountPage(query: Record<string, string> = {}) {
  router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/event-model', name: 'event-model', component: EventModelPage }],
  })
  await router.push({ path: '/event-model', query })
  await router.isReady()

  return mount(EventModelPage, {
    global: {
      plugins: [router],
      stubs: { teleport: true, ElSelect: true, ElOption: true },
    },
  })
}

beforeEach(() => {
  setActivePinia(createPinia())
  // The page lists the console's models on mount (bobcat#799). These cases seed the store directly, so
  // the console never answers: a fetch that never settles, rather than one that reaches the network.
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})))
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('EventModelPage', () => {
  it('renders the descriptor through the shared renderer, coloured by run evidence', async () => {
    seedStores()
    const wrapper = await mountPage()

    const slice = wrapper.find('[data-slice="CreditWallet"]')
    expect(slice.exists()).toBe(true)
    // The bound spec passed cleanly in the seeded run, so the slice reads as passed.
    expect(slice.attributes('data-outcome')).toBe('passed')
  })

  it('clicking the slice opens its bound scenarios with step results and drift', async () => {
    seedStores()
    const wrapper = await mountPage()

    await wrapper.find('.em-slice-name').trigger('click')

    const drilldown = wrapper.find('[data-testid="slice-drilldown"]')
    expect(drilldown.exists()).toBe(true)
    expect(drilldown.text()).toContain('Wallet/Crediting a wallet')
    expect(wrapper.find('[data-testid="spec-verdict"]').text()).toContain('CleanPass')
    expect(drilldown.text()).toContain('CreditWallet is received')
    // WalletSummary was touched but is not declared by the slice — the drift call-out.
    expect(wrapper.find('[data-testid="undeclared-touches"]').text()).toContain('1 touched type(s)')
  })

  it('draws a Query slice and labels it a read, with the roles its pattern rules out', async () => {
    // Wolverine 6.50's read-only GET (jasperfx#1003), here one that appends an event anyway.
    const model = useEventModelStore()
    model.descriptor = {
      name: 'Wallets',
      slices: [
        {
          name: 'GetWallet',
          pattern: 'Query',
          elements: [],
          edges: [],
          specifications: [],
          patternViolations: [
            { origin: 'PatternViolation', role: 'EmittedEvents', text: 'Query slice GetWallet emits WalletRead' },
          ],
        },
      ],
    }
    model.status = 'loaded'
    const wrapper = await mountPage()

    expect(wrapper.find('[data-slice="GetWallet"]').exists()).toBe(true)
    await wrapper.find('.em-slice-name').trigger('click')

    const tag = wrapper.find('[data-testid="slice-pattern"]')
    expect(tag.attributes('data-pattern')).toBe('Query')
    expect(tag.text()).toContain('Query — a read, no state change')
    const violations = wrapper.findAll('[data-testid="pattern-violation"]')
    expect(violations).toHaveLength(1)
    expect(violations[0]!.text()).toContain('emits WalletRead')
  })

  it('says so when nothing has been published', async () => {
    const model = useEventModelStore()
    model.status = 'absent'
    expect((await mountPage()).find('[data-testid="event-model-absent"]').exists()).toBe(true)
  })
})

/**
 * Issue #296 — the viewport in the URL. The renderer reports where the reader is; this page is
 * what decides that "where" belongs in the route query, so a link to a part of a 106-slice model
 * can be pasted into a PR.
 */
// The page coalesces URL writes to one per 100ms (`history.replaceState` is rate-limited by
// browsers and a drag reports every scroll frame), so a discrete action after the canvas's
// mount-time report lands on the trailing edge rather than immediately.
async function settleUrl() {
  await new Promise((resolve) => setTimeout(resolve, 160))
  await flushPromises()
}

describe('EventModelPage — viewport in the URL (#296)', () => {
  it('mirrors zoom, offsets, focus and selection into the route query', async () => {
    seedStores()
    const wrapper = await mountPage()

    // Select the slice, then focus it — the two controls the canvas exposes.
    await wrapper.find('.em-slice-name').trigger('click')
    await wrapper.find('[data-testid="focus-selection"]').trigger('click')
    await settleUrl()

    expect(router.currentRoute.value.query.focus).toBe('slice:CreditWallet')
    expect(router.currentRoute.value.query.sel).toBe('slice:CreditWallet')
    expect(router.currentRoute.value.query.z).toBeDefined()
  })

  it('lands on the same view when that URL is reloaded', async () => {
    seedStores()
    const wrapper = await mountPage({ z: '0.55', x: '400', y: '0', focus: 'slice:CreditWallet' })
    await flushPromises()

    // The focus survived the round trip and is on the canvas, not merely in the query bag.
    expect(wrapper.find('[data-testid="event-model-crumbs"]').text()).toContain('CreditWallet')
    expect(wrapper.find('[data-slice="CreditWallet"]').attributes('data-dimmed')).toBeUndefined()
  })

  it('clears a focus out of the URL rather than leaving its crumb there for ever', async () => {
    seedStores()
    const wrapper = await mountPage({ focus: 'slice:CreditWallet' })
    await flushPromises()

    await wrapper.find('[data-testid="focus-clear"]').trigger('click')
    await settleUrl()

    expect(router.currentRoute.value.query.focus).toBeUndefined()
  })

  it('replaces rather than pushes, so Back does not walk every notch of a zoom', async () => {
    seedStores()
    const wrapper = await mountPage()
    const push = vi.spyOn(router, 'push')

    await wrapper.find('.em-zoom-out').trigger('click')
    await settleUrl()

    expect(router.currentRoute.value.query.z).toBe('0.85')
    expect(push).not.toHaveBeenCalled()
  })

  it('ignores a hand-edited URL rather than landing on a canvas scrolled to NaN', async () => {
    seedStores()
    const wrapper = await mountPage({ z: 'banana', focus: 'lane:Nope' })
    await flushPromises()

    expect(wrapper.find('[data-testid="event-model-crumbs"]').exists()).toBe(false)
    expect(wrapper.find('.em-zoom-level').text()).toBe('100%')
  })
})

/**
 * bobcat#799 — several models on one console. The walkthrough's canvas opened on CritterCrush, pushed 13
 * days earlier from another repository, while every run on the board was BankAccountES: no switcher, and
 * nothing on the header to say the model was stale.
 */
describe('EventModelPage — which model, and where it came from (#799)', () => {
  const day = 24 * 60 * 60 * 1000
  // Half a minute past each mark, so the page's clock (read at mount, a moment before these) still
  // lands in the same bucket.
  const ago = (ms: number) => new Date(Date.now() - ms - 30_000).toISOString()

  const listing = () => ({
    current: 'CritterCrush',
    preferred: 'BankAccount',
    models: [
      {
        name: 'CritterCrush',
        lastPushedAt: ago(13 * day),
        slices: 17,
        hasRunsOnBoard: false,
        sources: [
          {
            source: 'CritterCrush-Specs',
            pushedAt: ago(13 * day),
            pushedAtIsFileTime: true,
            root: null,
            repository: null,
            branch: null,
          },
        ],
      },
      {
        name: 'BankAccount',
        lastPushedAt: ago(10 * 60 * 1000),
        slices: 13,
        hasRunsOnBoard: true,
        sources: [
          {
            source: 'BankAccountES-Tests',
            pushedAt: ago(10 * 60 * 1000),
            pushedAtIsFileTime: false,
            root: '/code/wolverine',
            repository: 'JasperFx/wolverine',
            branch: 'main',
          },
          {
            source: 'default',
            pushedAt: ago(3 * 60 * 60 * 1000),
            pushedAtIsFileTime: false,
            root: '/code/wolverine',
            repository: 'JasperFx/wolverine',
            branch: null,
          },
        ],
      },
    ],
  })

  const named = (name: string): EventModelDescriptor => ({
    name,
    slices: [{ name: `${name}Slice`, pattern: 'Command', elements: [], edges: [], specifications: [] }],
  })

  /** A console holding both models; a GET for any other name is a 404, as the real one answers. */
  function stubConsole() {
    const fetch = vi.fn(async (url: string) => {
      if (url === '/api/event-models') return new Response(JSON.stringify(listing()), { status: 200 })
      const name = new URL(url, 'http://console').searchParams.get('name') ?? 'CritterCrush'
      return ['CritterCrush', 'BankAccount'].includes(name)
        ? new Response(JSON.stringify(named(name)), { status: 200 })
        : new Response(null, { status: 404 })
    })
    vi.stubGlobal('fetch', fetch)
    return fetch
  }

  function modelSwitcher(wrapper: Awaited<ReturnType<typeof mountPage>>) {
    return wrapper
      .findAllComponents({ name: 'ElSelect' })
      .find((c) => c.attributes('data-testid') === 'model-switcher')
  }

  it('opens on the preferred model rather than the one pushed last', async () => {
    const fetch = stubConsole()
    const wrapper = await mountPage()
    await flushPromises()

    expect(fetch).toHaveBeenCalledWith('/api/event-model?name=BankAccount')
    expect(wrapper.find('[data-testid="model-name"]').text()).toContain('BankAccount')
    expect(wrapper.find('[data-slice="BankAccountSlice"]').exists()).toBe(true)
  })

  it('says when each source was last pushed, and from where', async () => {
    stubConsole()
    const wrapper = await mountPage()
    await flushPromises()

    const sources = wrapper.findAll('[data-testid="model-source"]')
    expect(sources.map((s) => s.attributes('data-source'))).toEqual(['BankAccountES-Tests', 'default'])
    expect(sources[0]!.text()).toContain('last pushed 10m ago from JasperFx/wolverine (main)')
    expect(sources[1]!.text()).toContain('last pushed 3h ago from JasperFx/wolverine')
    expect(wrapper.find('[data-testid="model-not-on-board"]').exists()).toBe(false)
  })

  it('a ?model= in the URL wins, and a stale model says it is stale', async () => {
    stubConsole()
    const wrapper = await mountPage({ model: 'CritterCrush' })
    await flushPromises()

    expect(wrapper.find('[data-testid="model-name"]').text()).toContain('CritterCrush')
    const source = wrapper.find('[data-testid="model-source"]')
    // Stored before the console kept push times, so it is the file's write time, and says so.
    expect(source.text()).toContain('last written 13d ago')
    expect(source.text()).not.toContain(' from ')
    expect(wrapper.find('[data-testid="model-not-on-board"]').exists()).toBe(true)
  })

  it('switching models puts the choice in the URL, drops the old viewport, and draws the other one', async () => {
    const fetch = stubConsole()
    const wrapper = await mountPage({ focus: 'slice:BankAccountSlice', z: '0.5' })
    await flushPromises()

    const switcher = modelSwitcher(wrapper)
    expect(switcher).toBeDefined()
    switcher!.vm.$emit('update:modelValue', 'CritterCrush')
    await flushPromises()

    expect(router.currentRoute.value.query.model).toBe('CritterCrush')
    expect(router.currentRoute.value.query.focus).toBeUndefined()
    expect(router.currentRoute.value.query.z).toBeUndefined()
    expect(fetch).toHaveBeenCalledWith('/api/event-model?name=CritterCrush')
    expect(wrapper.find('[data-slice="CritterCrushSlice"]').exists()).toBe(true)
  })

  it('offers no switcher when the console holds one model', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) =>
        url === '/api/event-models'
          ? new Response(JSON.stringify({ ...listing(), models: [listing().models[1]] }), { status: 200 })
          : new Response(JSON.stringify(named('BankAccount')), { status: 200 })
      )
    )
    const wrapper = await mountPage()
    await flushPromises()

    expect(modelSwitcher(wrapper)).toBeUndefined()
    expect(wrapper.find('[data-testid="model-provenance"]').exists()).toBe(true)
  })

  it('a URL naming a model the console does not hold says so, and keeps the switcher', async () => {
    stubConsole()
    const wrapper = await mountPage({ model: 'Gone' })
    await flushPromises()

    expect(wrapper.find('[data-testid="event-model-unknown"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="event-model-absent"]').exists()).toBe(false)
    expect(modelSwitcher(wrapper)).toBeDefined()
  })
})
