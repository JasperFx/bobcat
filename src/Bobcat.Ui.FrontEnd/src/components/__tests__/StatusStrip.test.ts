import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import StatusStrip from '../StatusStrip.vue'
import { relayToStore } from '@/messages/relayToStore'
import { useRunsStore } from '@/stores/runs-store'
import { useBoardStore } from '@/stores/board-store'
import { useConnectionStore } from '@/stores/connection-store'
import type { RunnerView } from '@/lib/runners'

/**
 * bobcat#786 — one status line on every page: the runner, the build only when it is broken (and then
 * the whole strip is red), the live run with a mini bar, `bobcat watch` when attached, and the
 * connection as a dot. It replaced a runner panel and a build banner the board alone had, and the
 * header's "connected" text. Everything on it is pushed (bobcat#784); the one request is the first load.
 */
const routerLinkStub = { template: '<a :href="typeof to === \'string\' ? to : \'#\'"><slot /></a>', props: ['to'] }
// element-plus's dropdown recurses on a sibling's re-render under happy-dom (not in a browser), and the
// dialog teleports; neither is what these tests are about. The dropdown stub keeps its trigger, which
// is what the strip shows, and drops the menu.
const dropdownStub = { template: '<div><slot /></div>' }
const stubs = { RouterLink: routerLinkStub, teleport: true, ElDropdown: dropdownStub, ElDropdownMenu: true, ElDropdownItem: true }

function runner(overrides: Partial<RunnerView> = {}): RunnerView {
  return {
    runnerId: 'r1', repository: '/code/bank', repositorySlug: 'JasperFx/bank', branch: 'main', suite: 'Bank.Tests',
    lane: 'projected', modes: ['cold', 'warm'], specs: 22, connected: true, status: null, statusDetail: null,
    registeredAt: '2026-10-26T11:48:00Z', lastSeenAt: '2026-10-26T12:00:00Z', commands: [], builtAt: '2026-10-26T11:57:00Z',
    ...overrides,
  }
}

function pushRunners(runners: RunnerView[]) {
  relayToStore({ type: 'runners_changed', data: { runners } })
}

function liveRun(runId: string, suite: string, startedAt: string, repository = '/code/bank', totalScenarios: number | null = 10) {
  useRunsStore().handleRunStarted({ runId, suite, repository, branch: 'main', mode: 'supervised', startedAt, totalScenarios })
}

describe('StatusStrip', () => {
  let requests: string[]

  beforeEach(() => {
    setActivePinia(createPinia())
    requests = []
    vi.useFakeTimers()
    vi.setSystemTime(Date.parse('2026-10-26T12:00:00Z'))
    vi.stubGlobal('fetch', (url: string) => {
      requests.push(url)
      return Promise.resolve({ ok: true, json: () => Promise.resolve([]) } as Response)
    })
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllGlobals()
  })

  function render() {
    return mount(StatusStrip, { global: { stubs } })
  }

  it('says how to attach a runner when none is, having asked once and never again', async () => {
    const strip = render()
    await flushPromises()
    expect(strip.find('[data-testid="strip-runner"]').text()).toContain('bobcat resident')
    const asked = requests.length

    await vi.advanceTimersByTimeAsync(30_000)
    expect(requests.length).toBe(asked)
  })

  it('shows the one runner with its state, age, warmth and build, linked to its page', async () => {
    const strip = render()
    await flushPromises()
    pushRunners([runner()])
    await flushPromises()

    const segment = strip.find('[data-testid="strip-runner"]')
    expect(segment.text()).toContain('Bank.Tests · listening · up 12m · warm · built 3m ago')
    expect(segment.find('a').attributes('href')).toBe('/runners/r1')
    expect(segment.find('[data-tone]').attributes('data-tone')).toBe('ok')
  })

  it('sums several runners up, in the worst of their tones', async () => {
    const strip = render()
    await flushPromises()
    pushRunners([runner(), runner({ runnerId: 'r2', suite: 'Other.Tests', connected: false, status: 'rebuilding' })])
    await flushPromises()

    const segment = strip.find('[data-testid="strip-runner"]')
    expect(segment.text()).toContain('2 · 1 listening')
    expect(segment.find('[data-tone]').attributes('data-tone')).toBe('wait')
  })

  it('shows the build only when it is broken, turns the whole strip red, and opens the errors on click', async () => {
    const strip = render()
    await flushPromises()
    expect(strip.find('[data-testid="strip-build"]').exists()).toBe(false)
    expect(strip.find('[data-testid="status-strip"]').attributes('data-broken')).toBe('false')

    relayToStore({
      type: 'build_status_changed',
      data: { builds: [{ suite: 'Bank.Tests', repository: '/code/bank', branch: 'main', state: 'build-failed', detail: 'Specs.cs(3,1): error CS1002', at: '2026-10-26T11:59:00Z' }] },
    })
    await flushPromises()

    expect(strip.find('[data-testid="status-strip"]').attributes('data-broken')).toBe('true')
    const build = strip.find('[data-testid="strip-build"]')
    expect(build.text()).toContain('Bank.Tests does not compile')
    expect(build.find('button').attributes('aria-label')).toBe('Show the compiler errors')

    await build.find('button').trigger('click')
    await flushPromises()
    expect(strip.find('[data-testid="build-errors"]').text()).toContain('CS1002')

    // A project that is only rebuilding is not a broken build: the strip stays calm.
    relayToStore({ type: 'build_status_changed', data: { builds: [{ suite: 'Bank.Tests', repository: '/code/bank', branch: 'main', state: 'building', detail: null, at: '2026-10-26T11:59:30Z' }] } })
    await flushPromises()
    expect(strip.find('[data-testid="strip-build"]').exists()).toBe(false)
    expect(strip.find('[data-testid="status-strip"]').attributes('data-broken')).toBe('false')
  })

  it('shows a live run with a mini bar, and drops it when the run finishes', async () => {
    liveRun('live', 'Bank.Tests', '2026-10-26T11:58:00Z')
    const strip = render()
    await flushPromises()

    const live = strip.find('[data-testid="strip-live"]')
    expect(live.text()).toContain('Bank.Tests')
    expect(live.find('[data-testid="live-progress"]').exists()).toBe(true)
    expect(live.find('[data-testid="live-counts"]').text()).toBe('0 / 10 · 0 passed')
    expect(live.find('[role="progressbar"]').attributes('aria-valuenow')).toBe('0')

    useRunsStore().handleRunFinished({ runId: 'live', exitCode: 0, passed: 10, failed: 0, passedOnRetry: 0, indeterminate: 0, finishedAt: '2026-10-26T12:00:00Z' })
    await flushPromises()
    expect(strip.find('[data-testid="strip-live"]').exists()).toBe(false)
  })

  it('offers a choice among several live runs, filtered by the board\'s repository filter', async () => {
    liveRun('a', 'Bank.Tests', '2026-10-26T11:58:00Z', '/code/bank')
    liveRun('b', 'Stoat.Specs', '2026-10-26T11:59:00Z', '/code/stoat')
    useRunsStore().learnRepositories([
      { repository: '/code/bank', repositorySlug: 'JasperFx/bank' },
      { repository: '/code/stoat', repositorySlug: 'JasperFx/stoat' },
    ])
    const strip = render()
    await flushPromises()

    // Newest first, with the count, as a picker.
    expect(strip.find('[data-testid="strip-live-picker"]').text()).toContain('Stoat.Specs (2 live)')

    useBoardStore().repositoryFilter = 'JasperFx/bank'
    await flushPromises()
    expect(strip.find('[data-testid="strip-live-picker"]').exists()).toBe(false)
    expect(strip.find('[data-testid="strip-live-link"]').text()).toBe('Bank.Tests')
  })

  it('shows bobcat watch when a build report has arrived', async () => {
    const strip = render()
    await flushPromises()
    expect(strip.find('[data-testid="strip-watch"]').exists()).toBe(false)

    relayToStore({ type: 'build_status_changed', data: { builds: [{ suite: 'Bank.Tests', repository: '/code/bank', branch: 'main', state: 'built', detail: null, at: '2026-10-26T11:58:00Z' }] } })
    await flushPromises()
    expect(strip.find('[data-testid="strip-watch"]').text()).toContain('Bank.Tests · built 2m ago')
  })

  it('shows the connection as a labelled dot', async () => {
    const strip = render()
    await flushPromises()
    const dot = strip.find('[data-testid="strip-connection"] [role="img"]')
    expect(dot.attributes('aria-label')).toBe('Connection: disconnected')

    useConnectionStore().setConnected()
    await flushPromises()
    expect(dot.attributes('data-status')).toBe('connected')
    expect(dot.attributes('aria-label')).toBe('Connection: connected')
  })
})
