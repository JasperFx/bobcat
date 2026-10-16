import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import RunnersPanel from '../RunnersPanel.vue'
import { resetRunners } from '@/composables/useRunners'
import type { RunnerView } from '@/lib/runners'

/**
 * bobcat#645 — before anything has run, the board still offers the attached runners and a button
 * that runs a whole project. Before it, a person had to run `dotnet test` once to get a page to
 * press a button on.
 */
const routerLinkStub = { template: '<a :href="to"><slot /></a>', props: ['to'] }

function runner(overrides: Partial<RunnerView> = {}): RunnerView {
  return {
    runnerId: 'r1', repository: '/code/bank', repositorySlug: 'JasperFx/bank', branch: 'main', suite: 'BankAccountES.Tests',
    lane: 'projected', modes: ['cold', 'warm'], specs: 22, connected: true, status: null, statusDetail: null,
    registeredAt: '', lastSeenAt: '', commands: [], ...overrides,
  }
}

function render() {
  return mount(RunnersPanel, { global: { stubs: { RouterLink: routerLinkStub, teleport: true } } })
}

describe('RunnersPanel', () => {
  let registered: RunnerView[]
  let posts: { url: string; body: Record<string, unknown> }[]
  let asked: string[]

  beforeEach(() => {
    registered = []
    posts = []
    asked = []
    localStorage.clear()
    resetRunners()
    vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
      asked.push(url)
      if (init?.method === 'POST') {
        posts.push({ url, body: JSON.parse(String(init.body)) })
        return Promise.resolve({ ok: true, status: 202, json: () => Promise.resolve({ commandId: 'c1', specs: [] }) } as Response)
      }
      return Promise.resolve({ ok: true, json: () => Promise.resolve(registered) } as Response)
    })
  })

  afterEach(() => vi.unstubAllGlobals())

  it('lists each runner with its state and a button that runs its whole project', async () => {
    registered = [runner(), runner({ runnerId: 'r2', suite: 'Other.Tests', connected: false, status: 'build-failed', specs: 3 })]
    const wrapper = render()
    await flushPromises()

    const rows = wrapper.findAll('[data-testid="runner-row"]')
    expect(rows).toHaveLength(2)
    expect(rows[0]!.text()).toContain('BankAccountES.Tests')
    expect(rows[0]!.text()).toContain('22 specs')
    expect(rows[0]!.text()).toContain('listening')
    expect(rows[1]!.text()).toContain('build failed')
    expect(rows[0]!.text()).toContain('Run all')
  })

  it('runs everything the runner registered on that runner, not on some other one', async () => {
    registered = [runner(), runner({ runnerId: 'r2', suite: 'Other.Tests' })]
    const wrapper = render()
    await flushPromises()

    await wrapper.findAll('[data-testid="run-button"]')[1]!.trigger('click')
    await flushPromises()
    expect(asked).toContain('/api/runners/for?all=true')
    expect(wrapper.findAll('[data-testid="runner-choice"]')).toHaveLength(1)

    await wrapper.find('[data-testid="run-send"]').trigger('click')
    await flushPromises()

    expect(posts).toHaveLength(1)
    expect(posts[0]!.url).toBe('/api/runners/r2/commands')
    expect(posts[0]!.body).toEqual({ all: true, mode: 'cold', by: 'bobcat-ui' })
  })

  it('says how to attach a runner when none is', async () => {
    const wrapper = render()
    await flushPromises()

    expect(wrapper.find('[data-testid="no-runners"]').text()).toContain('bobcat resident')
  })
})
