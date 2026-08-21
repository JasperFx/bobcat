import { beforeEach, describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { useRunsStore } from '@/stores/runs-store'
import SupervisorTopology from '../SupervisorTopology.vue'
import LaneStrip from '../LaneStrip.vue'

/**
 * Issue #84 — the two topology views render what the store folded: lanes with what they are
 * on, the recycle timeline, and worker faults with exit code and last standard error. Fed
 * through the store, as the real views are, rather than through hand-built props.
 */
const RUN = '9a1f1a1e-0000-0000-0000-000000000086'

function seed() {
  const store = useRunsStore()
  store.handleRunStarted({
    runId: RUN,
    suite: 'Supervised Suite',
    repository: '/repo',
    branch: 'main',
    mode: 'supervised',
    startedAt: '2026-08-21T10:00:00Z',
    totalScenarios: 2,
  })
  store.handleLaneStarted({ runId: RUN, lane: 0, uids: ['Orders/places an order'], at: '2026-08-21T10:00:01Z' })
  store.handleLaneStarted({ runId: RUN, lane: 1, uids: ['Payments/charges'], at: '2026-08-21T10:00:01Z' })
  store.handleScenarioStarted({
    runId: RUN,
    uid: 'Orders/places an order',
    feature: 'Orders',
    scenario: 'places an order',
    attempt: 1,
    at: '2026-08-21T10:00:02Z',
  })
  store.handleLaneFinished({ runId: RUN, lane: 1, outcomes: 0, crashed: true, at: '2026-08-21T10:00:03Z' })
  store.handleWorkerFaulted({
    runId: RUN,
    lane: 1,
    fault: 'the worker exited with code 139',
    exitCode: 139,
    standardError: 'Segmentation fault (core dumped)',
    at: '2026-08-21T10:00:03Z',
  })
  store.handleResourceRecycled({ runId: RUN, resource: 'rabbit', at: '2026-08-21T10:00:04Z' })
  return store.runById(RUN)!
}

describe('SupervisorTopology', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('renders nothing for a run with no topology', () => {
    const store = useRunsStore()
    store.handleRunStarted({
      runId: RUN,
      suite: 'In-process',
      repository: '/repo',
      branch: null,
      mode: 'in-process',
      startedAt: '2026-08-21T10:00:00Z',
      totalScenarios: 1,
    })

    const wrapper = mount(SupervisorTopology, { props: { run: store.runById(RUN)! } })
    expect(wrapper.find('[data-testid="supervisor-topology"]').exists()).toBe(false)
  })

  it('renders every lane with its status and current scenario, the recycle, and the fault with exit code and stderr', () => {
    const run = seed()
    const wrapper = mount(SupervisorTopology, { props: { run } })

    const rows = wrapper.findAll('tbody tr')
    expect(rows).toHaveLength(2)
    expect(rows[0]!.attributes('data-status')).toBe('running')
    expect(rows[0]!.text()).toContain('places an order')
    expect(rows[1]!.attributes('data-status')).toBe('crashed')

    expect(wrapper.find('[data-testid="recycles"]').text()).toContain('rabbit')

    const faults = wrapper.find('[data-testid="worker-faults"]')
    expect(faults.text()).toContain('lane 1')
    expect(faults.text()).toContain('exit code 139')
    expect(faults.text()).toContain('the worker exited with code 139')
    expect(faults.find('pre').text()).toBe('Segmentation fault (core dumped)')
  })
})

describe('LaneStrip', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('shows a chip per lane naming what it is on, plus recycle and fault counts', () => {
    const run = seed()
    const wrapper = mount(LaneStrip, { props: { run } })

    const chips = wrapper.findAll('.bm-lane-chip')
    expect(chips).toHaveLength(2)
    expect(chips[0]!.attributes('data-status')).toBe('running')
    expect(chips[0]!.text()).toContain('places an order')
    expect(chips[1]!.attributes('data-status')).toBe('crashed')
    expect(chips[1]!.text()).toContain('crashed')

    expect(wrapper.find('[data-testid="recycle-count"]').text()).toBe('1 recycle')
    expect(wrapper.find('[data-testid="fault-count"]').text()).toBe('1 worker fault')
  })

  it('marks a lane on its second pass', () => {
    const run = seed()
    useRunsStore().handleLaneStarted({
      runId: RUN,
      lane: 0,
      uids: ['Orders/places an order'],
      at: '2026-08-21T10:00:10Z',
    })

    const wrapper = mount(LaneStrip, { props: { run } })
    expect(wrapper.find('[data-lane="0"] .bm-lane-pass').text()).toBe('×2')
  })
})
