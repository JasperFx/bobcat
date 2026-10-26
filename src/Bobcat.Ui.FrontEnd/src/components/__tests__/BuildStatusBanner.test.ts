import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import BuildStatusBanner from '../BuildStatusBanner.vue'
import { relayToStore } from '@/messages/relayToStore'

/**
 * bobcat#784 — the banner follows the console's build_status_changed push, and never polls: the
 * one request it makes is the first load, and a report that arrives later is on the page with no
 * request at all.
 */
describe('BuildStatusBanner', () => {
  let requests: string[]

  beforeEach(() => {
    setActivePinia(createPinia())
    requests = []
    vi.useFakeTimers()
    vi.stubGlobal('fetch', (url: string) => {
      requests.push(url)
      return Promise.resolve({ ok: true, json: () => Promise.resolve([]) } as Response)
    })
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllGlobals()
  })

  it('shows a failed build the console pushed, having asked once and never again', async () => {
    const wrapper = mount(BuildStatusBanner)
    await flushPromises()
    expect(wrapper.find('[data-testid="build-status"]').exists()).toBe(false)
    const asked = requests.length

    relayToStore({
      type: 'build_status_changed',
      data: { builds: [{ suite: 'Bank.Tests', repository: '/code/bank', branch: 'main', state: 'build-failed', detail: 'Specs.cs(3,1): error CS1002', at: '2026-10-26T10:00:01Z' }] },
    })
    await flushPromises()

    expect(wrapper.find('[data-testid="build-problem"]').text()).toContain('Bank.Tests does not compile')
    expect(wrapper.find('[data-testid="build-errors"]').text()).toContain('CS1002')

    await vi.advanceTimersByTimeAsync(30_000)
    expect(requests.length).toBe(asked)

    relayToStore({ type: 'build_status_changed', data: { builds: [{ suite: 'Bank.Tests', repository: '/code/bank', branch: 'main', state: 'built', detail: null, at: '2026-10-26T10:00:09Z' }] } })
    await flushPromises()
    expect(wrapper.find('[data-testid="build-status"]').exists()).toBe(false)
  })
})
