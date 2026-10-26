import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import RunnerView from '../RunnerView.vue'
import { relayToStore } from '@/messages/relayToStore'
import type { RunnerSpecView, RunnerView as Runner } from '@/lib/runners'

/**
 * bobcat#784 — the runner page used to load once and never refresh. Now the console pushes the
 * runner list on every change, and the page takes its runner from each push; a runner that has
 * registered again (a rebuild) has its specifications loaded again, since they may have changed.
 */
const routerLinkStub = { template: '<a :href="to"><slot /></a>', props: ['to'] }

function runner(overrides: Partial<Runner> = {}): Runner {
  return {
    runnerId: 'r1', repository: '/code/bank', repositorySlug: 'JasperFx/bank', branch: 'main', suite: 'Bank.Tests',
    lane: 'projected', modes: ['cold'], specs: 1, connected: false, status: 'rebuilding', statusDetail: null,
    registeredAt: '2026-10-26T10:00:00Z', lastSeenAt: '2026-10-26T10:00:00Z', commands: [], ...overrides,
  }
}

describe('RunnerView follows the pushed runner list', () => {
  let specs: RunnerSpecView[]
  let specRequests: number

  beforeEach(() => {
    setActivePinia(createPinia())
    specs = [{ identity: 'Accounts/opened', feature: 'Accounts', scenario: 'opened', steps: ['Given a customer'] }]
    specRequests = 0
    vi.stubGlobal('fetch', (url: string) => {
      if (url.endsWith('/specs')) {
        specRequests++
        return Promise.resolve({ ok: true, json: () => Promise.resolve(specs) } as Response)
      }
      if (url === '/api/runners/r1') return Promise.resolve({ ok: true, json: () => Promise.resolve(runner()) } as Response)
      return Promise.resolve({ ok: true, json: () => Promise.resolve([]) } as Response)
    })
  })

  afterEach(() => vi.unstubAllGlobals())

  it('shows the pushed state without asking again, and reloads the specs when the runner registers again', async () => {
    const wrapper = mount(RunnerView, { props: { runnerId: 'r1' }, global: { stubs: { RouterLink: routerLinkStub, teleport: true } } })
    await flushPromises()
    expect(wrapper.find('[data-testid="runner-page"]').text()).toContain('rebuilding')
    expect(specRequests).toBe(1)

    relayToStore({ type: 'runners_changed', data: { runners: [runner({ connected: true, status: null })] } })
    await flushPromises()
    expect(wrapper.find('[data-testid="runner-page"]').text()).toContain('listening')
    expect(specRequests).toBe(1)

    specs = [...specs, { identity: 'Accounts/closed', feature: 'Accounts', scenario: 'closed', steps: [] }]
    relayToStore({ type: 'runners_changed', data: { runners: [runner({ connected: true, status: null, specs: 2, registeredAt: '2026-10-26T10:05:00Z' })] } })
    await flushPromises()
    expect(specRequests).toBe(2)
    expect(wrapper.findAll('[data-testid="spec"]')).toHaveLength(2)
  })
})
