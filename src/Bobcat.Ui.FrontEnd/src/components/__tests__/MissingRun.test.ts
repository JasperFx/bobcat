import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import MissingRun from '../MissingRun.vue'

/**
 * bobcat#646 — the board keeps the latest runs of each suite, so pressing run buttons evicts the page
 * a person came from. It says so, and where the archive is, rather than "Unknown run".
 */
describe('MissingRun', () => {
  afterEach(() => vi.unstubAllGlobals())

  function answering(body: unknown) {
    vi.stubGlobal('fetch', () => Promise.resolve({ ok: true, json: () => Promise.resolve(body) } as Response))
  }

  it('says an evicted run was evicted, why, and where its archive is', async () => {
    answering({ state: 'ejected', archive: '/home/me/.bobcat/monitor/runs/ejected/r1.ndjson', retainedRuns: 10 })
    const wrapper = mount(MissingRun, { props: { runId: 'r1' } })
    await flushPromises()

    const text = wrapper.find('[data-testid="run-evicted"]').text()
    expect(text).toContain('no longer on the board')
    expect(text).toContain('latest 10 finished runs of each suite')
    expect(text).toContain('/home/me/.bobcat/monitor/runs/ejected/r1.ndjson')
  })

  it('is an unknown run only when the console never had it', async () => {
    answering({ state: 'unknown', archive: null, retainedRuns: 10 })
    const wrapper = mount(MissingRun, { props: { runId: 'r1' } })
    await flushPromises()

    expect(wrapper.find('[data-testid="run-unknown"]').exists()).toBe(true)
  })
})
