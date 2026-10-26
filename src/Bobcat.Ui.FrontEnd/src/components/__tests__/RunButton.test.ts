import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { relayToStore } from '@/messages/relayToStore'
import RunButton from '../RunButton.vue'
import type { RunnerView } from '@/lib/runners'

/**
 * stoat#78 — the button a person presses to run a spec on a resident runner. What is under test:
 * a missing runner is said plainly with the command that starts one, the runner is chosen with
 * where it is, and the command is followed to the run it caused.
 */
const routerLinkStub = { template: '<a :href="to"><slot /></a>', props: ['to'] }

function runner(overrides: Partial<RunnerView> = {}): RunnerView {
  return {
    runnerId: 'r1', repository: '/code/stoat', repositorySlug: 'JasperFx/stoat', branch: 'main', suite: 'Stoat.Specs',
    lane: 'gherkin', modes: ['cold'], specs: 3, connected: true, status: null, statusDetail: null,
    registeredAt: '', lastSeenAt: '', commands: [], ...overrides,
  }
}

function render(target = { specs: ['F/a'] }, suite: string | null = null) {
  return mount(RunButton, {
    props: { target, label: 'Run again', suite },
    // Teleport stubbed, so the dialog renders inside the wrapper and the test needs no `document`.
    global: { stubs: { RouterLink: routerLinkStub, teleport: true } },
  })
}

describe('RunButton', () => {
  let posts: { url: string; body: unknown }[]
  let candidates: RunnerView[]
  let followed: RunnerView | null

  beforeEach(() => {
    setActivePinia(createPinia())
    posts = []
    candidates = []
    followed = null
    localStorage.clear()
    vi.stubGlobal('fetch', (url: string, init?: RequestInit) => {
      if (init?.method === 'POST') {
        posts.push({ url, body: JSON.parse(String(init.body)) })
        return Promise.resolve({ ok: true, status: 202, json: () => Promise.resolve({ commandId: 'c1', specs: ['F/a'] }) } as Response)
      }
      const body = url.startsWith('/api/runners/for') ? candidates : followed
      return Promise.resolve({ ok: true, json: () => Promise.resolve(body) } as Response)
    })
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllGlobals()
  })

  it('says plainly when no runner can run it, and how to start one', async () => {
    const wrapper = render()
    await wrapper.find('[data-testid="run-button"]').trigger('click')
    await flushPromises()

    const empty = wrapper.find('[data-testid="no-runner"]').text()
    expect(empty).toContain('No runner can run this')
    expect(empty).toContain('bobcat resident <test host>')
    wrapper.unmount()
  })

  // bobcat#652: pressing Run on an earlier run's page for a spec renamed since blamed another suite
  it("says the suite's own runner no longer has a spec renamed since the run, rather than to start one", async () => {
    followed = [runner()] as unknown as RunnerView
    const wrapper = render({ specs: ['F/renamed away'] }, 'Stoat.Specs')
    await wrapper.find('[data-testid="run-button"]').trigger('click')
    await flushPromises()

    const said = wrapper.find('[data-testid="renamed-away"]').text()
    expect(said).toContain("Stoat.Specs's runner no longer has F/renamed away")
    expect(said).toContain('renamed or removed since this run')
    expect(wrapper.text()).not.toContain('bobcat resident <test host>')
    wrapper.unmount()
  })

  it('shows each runner with its repository, branch and why it is not listening', async () => {
    candidates = [runner(), runner({ runnerId: 'r2', repository: '/code/stoat-wt', branch: 'feature', connected: false, status: 'rebuilding' })]
    const wrapper = render()
    await wrapper.find('[data-testid="run-button"]').trigger('click')
    await flushPromises()

    const choices = wrapper.findAll('[data-testid="runner-choice"]').map((c) => c.text())
    expect(choices[0]).toContain('JasperFx/stoat @ main')
    expect(choices[0]).toContain('listening')
    expect(choices[1]).toContain('@ feature')
    expect(choices[1]).toContain('rebuilding')
    expect(choices[1]).toContain('/code/stoat-wt')
    wrapper.unmount()
  })

  it('sends cold to the chosen runner and follows the command to its run from the pushed runner list', async () => {
    candidates = [runner()]
    const wrapper = render()
    await wrapper.find('[data-testid="run-button"]').trigger('click')
    await flushPromises()

    await wrapper.find('[data-testid="run-send"]').trigger('click')
    await flushPromises()
    expect(posts).toEqual([{ url: '/api/runners/r1/commands', body: { specs: ['F/a'], mode: 'cold', by: 'bobcat-ui' } }])
    expect(wrapper.find('[data-testid="run-progress"]').text()).toContain('queued')

    // bobcat#784: the console pushes the runner list as the command moves; nothing is polled.
    const command = { commandId: 'c1', kind: 'run', specs: ['F/a'], mode: 'cold', state: 'sent', reason: null, requestedBy: 'stoat-ui', requestedAt: '', acknowledgedAt: null, runId: null }
    relayToStore({ type: 'runners_changed', data: { runners: [runner({ commands: [command] })] } })
    await flushPromises()
    expect(wrapper.find('[data-testid="run-progress"]').text()).toContain('sent')

    relayToStore({ type: 'runners_changed', data: { runners: [runner({ commands: [{ ...command, state: 'accepted', acknowledgedAt: '', runId: 'run-9' }] })] } })
    await flushPromises()

    expect(wrapper.find('[data-testid="run-progress"]').text()).toContain('running')
    expect(wrapper.find('[data-testid="run-link"]').attributes('href')).toBe('/runs/run-9')
    wrapper.unmount()
  })

  it('asks for a new runner with a restart, not a run', async () => {
    candidates = [runner()]
    const wrapper = render()
    await wrapper.find('[data-testid="run-button"]').trigger('click')
    await flushPromises()

    await wrapper.find('[data-testid="force-new-runner"]').trigger('click')
    await flushPromises()
    expect(posts.map((p) => p.body)).toEqual([{ restart: true, by: 'bobcat-ui' }])
    wrapper.unmount()
  })
})
