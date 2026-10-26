import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { useRunsStore } from '@/stores/runs-store'
import { relayToStore } from '@/messages/relayToStore'
import DashboardView from '../DashboardView.vue'

/**
 * bobcat#784 — a run whose publisher died while the console was up used to show as live forever:
 * a progress bar, no Eject, never evicted. The console now pushes run_orphaned, and the card
 * changes hands: no bar, an Eject button.
 */
const routerLinkStub = { template: '<a><slot /></a>', props: ['to'] }
const stubs = { RouterLink: routerLinkStub, teleport: true, ElSelect: true, ElOption: true }
const RUN = '9a1f1a1e-0000-0000-0000-000000000784'

describe('an orphaned run on the board', () => {
  let deleted: string[]

  beforeEach(() => {
    setActivePinia(createPinia())
    deleted = []
    vi.useFakeTimers()
    vi.setSystemTime(Date.parse('2026-10-26T12:00:00Z'))
    vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
      if (init?.method === 'DELETE') deleted.push(url)
      return Promise.resolve({ ok: true, status: 204, json: () => Promise.resolve([]) } as Response)
    })
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllGlobals()
  })

  it('loses its progress bar and gains an Eject when the console pushes run_orphaned', async () => {
    useRunsStore().handleRunStarted({
      runId: RUN, suite: 'Bank.Tests', repository: '/repo', branch: 'main', mode: 'in-process',
      startedAt: '2026-10-26T11:50:00Z', totalScenarios: 4,
    })
    const page = mount(DashboardView, { global: { stubs } })
    await flushPromises()

    const card = () => page.find('.bm-run-card')
    expect(card().attributes('data-live')).toBe('true')
    expect(card().find('.bm-eject').exists()).toBe(false)

    relayToStore({ type: 'run_orphaned', data: { runId: RUN, lastEventAt: '2026-10-26T11:52:00Z' } })
    await flushPromises()

    expect(card().attributes('data-live')).toBe('false')
    expect(card().text()).toContain('orphaned')
    expect(card().find('.bm-eject').exists()).toBe(true)

    await card().find('.bm-eject').trigger('click')
    await flushPromises()
    expect(deleted).toEqual([`/api/runs/${RUN}`])
    expect(useRunsStore().runById(RUN)).toBeUndefined()
  })
})
