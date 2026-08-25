import { beforeEach, describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
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
const mountPage = () =>
  mount(EventModelPage, { global: { stubs: { teleport: true, ElSelect: true, ElOption: true } } })

beforeEach(() => {
  setActivePinia(createPinia())
})

describe('EventModelPage', () => {
  it('renders the descriptor through the shared renderer, coloured by run evidence', () => {
    seedStores()
    const wrapper = mountPage()

    const slice = wrapper.find('[data-slice="CreditWallet"]')
    expect(slice.exists()).toBe(true)
    // The bound spec passed cleanly in the seeded run, so the slice reads as passed.
    expect(slice.attributes('data-outcome')).toBe('passed')
  })

  it('clicking the slice opens its bound scenarios with step results and drift', async () => {
    seedStores()
    const wrapper = mountPage()

    await wrapper.find('.em-slice-name').trigger('click')

    const drilldown = wrapper.find('[data-testid="slice-drilldown"]')
    expect(drilldown.exists()).toBe(true)
    expect(drilldown.text()).toContain('Wallet/Crediting a wallet')
    expect(wrapper.find('[data-testid="spec-verdict"]').text()).toContain('CleanPass')
    expect(drilldown.text()).toContain('CreditWallet is received')
    // WalletSummary was touched but is not declared by the slice — the drift call-out.
    expect(wrapper.find('[data-testid="undeclared-touches"]').text()).toContain('1 touched type(s)')
  })

  it('says so when nothing has been published', () => {
    const model = useEventModelStore()
    model.status = 'absent'
    expect(mountPage().find('[data-testid="event-model-absent"]').exists()).toBe(true)
  })
})
