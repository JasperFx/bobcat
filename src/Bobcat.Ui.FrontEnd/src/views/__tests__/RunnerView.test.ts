import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import RunnerView from '../RunnerView.vue'
import type { RunnerSpecView, RunnerView as Runner } from '@/lib/runners'

/**
 * bobcat#644 — a runner's specifications, previewed before anything has run: by feature, with the
 * steps each plans, and a run button for the runner, each feature and each specification.
 */
const routerLinkStub = { template: '<a :href="to"><slot /></a>', props: ['to'] }

const runner: Runner = {
  runnerId: 'r1', repository: '/code/bank', repositorySlug: 'JasperFx/bank', branch: 'main', suite: 'BankAccountES.Tests',
  lane: 'projected', modes: ['cold', 'warm'], specs: 3, connected: true, status: null, statusDetail: null,
  registeredAt: '', lastSeenAt: '', commands: [],
}

describe('RunnerView', () => {
  let specs: RunnerSpecView[]

  beforeEach(() => {
    setActivePinia(createPinia())
    specs = [
      { identity: 'Accounts/an account is opened', feature: 'Accounts', scenario: 'an account is opened', steps: ['Given a customer', 'When they open an account'] },
      { identity: 'Accounts/a deposit', feature: 'Accounts', scenario: 'a deposit', steps: ['When {amount} is deposited'] },
      { identity: 'Withdrawals/an overdraft is refused', feature: 'Withdrawals', scenario: 'an overdraft is refused', steps: [] },
    ]
    vi.stubGlobal('fetch', (url: string) => {
      const body = url.endsWith('/specs') ? specs : runner
      return Promise.resolve({ ok: true, json: () => Promise.resolve(body) } as Response)
    })
  })

  afterEach(() => vi.unstubAllGlobals())

  function render() {
    return mount(RunnerView, { props: { runnerId: 'r1' }, global: { stubs: { RouterLink: routerLinkStub, teleport: true } } })
  }

  it('lists the specifications by feature with their planned steps, before anything has run', async () => {
    const wrapper = render()
    await flushPromises()

    const features = wrapper.findAll('[data-testid="feature"]')
    expect(features).toHaveLength(2)
    expect(features[0]!.text()).toContain('Accounts')
    expect(features[0]!.findAll('[data-testid="spec"]')).toHaveLength(2)
    expect(features[0]!.text()).toContain('Given a customer')
    expect(features[0]!.text()).toContain('When {amount} is deposited')
    expect(wrapper.find('[data-testid="no-outline"]').exists()).toBe(false)
  })

  it('offers a run for the whole runner, each feature and each specification', async () => {
    const wrapper = render()
    await flushPromises()

    const labels = wrapper.findAll('[data-testid="run-button"]').map((b) => b.text())
    // bobcat#787: every button says the mode it will send.
    expect(labels).toContain('▶ Run all (3) · cold')
    expect(labels).toContain('▶ Run feature (2) · cold')
    expect(labels.filter((l) => l === '▶ Run · cold')).toHaveLength(3)
  })

  it('says so when the runner registered no steps, rather than showing bare titles as if that were all', async () => {
    specs = specs.map((s) => ({ ...s, steps: [] }))
    const wrapper = render()
    await flushPromises()

    expect(wrapper.find('[data-testid="no-outline"]').exists()).toBe(true)
  })

  it("draws a step's constant table as a grid under that step, and nothing under a step without one (bobcat#827)", async () => {
    specs = [
      {
        identity: 'Underwriting/the underwriting rules', feature: 'Underwriting', scenario: 'the underwriting rules',
        steps: ['Given a policy', 'Then the underwriting decisions are'],
        tables: [{ step: 1, headers: ['Credit Score', 'Decision'], rows: [['579', 'Decline'], ['580', 'Approve']] }],
      },
      { identity: 'Underwriting/a plain one', feature: 'Underwriting', scenario: 'a plain one', steps: ['Then the total is {total}'] },
    ]
    const wrapper = render()
    await flushPromises()

    const tables = wrapper.findAll('[data-testid="spec-table"]')
    expect(tables).toHaveLength(1)
    expect(tables[0]!.findAll('th').map((th) => th.text())).toEqual(['Credit Score', 'Decision'])
    expect(tables[0]!.findAll('tbody tr')).toHaveLength(2)
    expect(tables[0]!.findAll('tbody tr')[1]!.findAll('td').map((td) => td.text())).toEqual(['580', 'Approve'])

    // Under its own step, not the first one
    const steps = wrapper.findAll('[data-testid="spec"]')[0]!.findAll('li')
    expect(steps[0]!.find('[data-testid="spec-table"]').exists()).toBe(false)
    expect(steps[1]!.find('[data-testid="spec-table"]').exists()).toBe(true)
  })
})
