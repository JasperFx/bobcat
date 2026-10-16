import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { useRunsStore } from '@/stores/runs-store'
import { resetRunners } from '@/composables/useRunners'
import DashboardView from '../DashboardView.vue'

/**
 * bobcat#646 — every commanded run's card read `BankAccountES.Tests · bobcat @ branch · xunit` with a
 * count, so a spec, a feature, a slice and the whole project were told apart only by the count. The
 * card now says what was run, how, and who asked, and while it runs how far through its selection.
 */
const routerLinkStub = { template: '<a><slot /></a>', props: ['to'] }

// element-plus's select recurses on a sibling's re-render under happy-dom (not in a browser: checked in
// Chromium), and the board's own selects are not what this tests.
const stubs = { RouterLink: routerLinkStub, teleport: true, ElSelect: true, ElOption: true }

describe('a commanded run on the board', () => {
  let page: VueWrapper | null = null

  beforeEach(() => {
    setActivePinia(createPinia())
    resetRunners()
    // As the board's own tests do: element-plus's select re-measures on every real tick in jsdom
    vi.useFakeTimers()
    vi.setSystemTime(Date.parse('2026-09-01T12:00:00Z'))
    vi.stubGlobal('fetch', (url: string) => {
      const body = url === '/api/runners'
        ? [{
            runnerId: 'r1', repository: '/repo', repositorySlug: null, branch: 'main', suite: 'BankAccountES.Tests',
            lane: 'projected', modes: ['cold', 'warm'], specs: 22, connected: true, status: null, statusDetail: null,
            registeredAt: '', lastSeenAt: '', commands: [{
              commandId: 'c1', kind: 'run', specs: ['Bank/opens', 'Bank/closes'], mode: 'warm', state: 'accepted',
              reason: null, requestedBy: 'bobcat-ui', requestedAt: '', acknowledgedAt: null, runId: 'cmd-run',
            }],
          }]
        : {}
      return Promise.resolve({ ok: true, json: () => Promise.resolve(body) } as Response)
    })
  })

  afterEach(() => {
    page?.unmount()
    page = null
    resetRunners()
    vi.useRealTimers()
    vi.unstubAllGlobals()
  })

  it('says what it was asked to run, and how far through its selection it is', async () => {
    useRunsStore().handleRunStarted({
      runId: 'cmd-run', suite: 'BankAccountES.Tests', repository: '/repo', branch: 'main', mode: 'xunit',
      startedAt: '2026-09-01T11:59:00Z', totalScenarios: null,
    })

    page = mount(DashboardView, { global: { stubs } })
    await flushPromises()

    expect(page.find('[data-testid="run-command"]').text())
      .toBe('Run 2 in feature Bank · warm · from bobcat-ui · 0 finished of 2 specs selected')
  })

  it('says nothing for a run nobody commanded', async () => {
    useRunsStore().handleRunStarted({
      runId: 'plain-run', suite: 'BankAccountES.Tests', repository: '/repo', branch: 'main', mode: 'xunit',
      startedAt: '2026-09-01T11:59:00Z', totalScenarios: null,
    })

    page = mount(DashboardView, { global: { stubs } })
    await flushPromises()
    expect(page.find('[data-testid="runners-panel"]').exists()).toBe(true)

    expect(page.find('[data-testid="run-command"]').exists()).toBe(false)
  })
})
