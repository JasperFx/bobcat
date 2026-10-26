import { describe, expect, it } from 'vitest'
import { featureCrumb, formatClock, runCrumb, runCrumbLabel, scenarioCrumb } from '../crumbs'
import type { RunState } from '@/stores/runs-store'

/**
 * bobcat#787 — the breadcrumb trail's run segment always carries the run's identity: when it
 * started, its tag, its branch. A trail reading `Runs › Wallets › run` says which suite, not which
 * of its ten runs.
 */
function run(overrides: Partial<RunState> = {}): RunState {
  return {
    runId: 'run-1',
    suite: 'Wallets',
    repository: '/code/wallets',
    branch: 'main',
    mode: 'in-process',
    startedAt: '2026-10-26T10:42:00Z',
    totalScenarios: null,
    finished: false,
    orphaned: false,
    exitCode: null,
    counts: null,
    finishedAt: null,
    lastEventAt: '2026-10-26T10:42:00Z',
    tag: 'build-512',
    session: null,
    command: null,
    eventsLoaded: true,
    scenarios: {},
    lanes: [],
    recycles: [],
    faults: [],
    stalls: [],
    progress: null,
    ...overrides,
  }
}

const NOW = Date.parse('2026-10-26T12:00:00Z')

describe('formatClock', () => {
  it('is the time alone today, and the date as well on another day', () => {
    const today = formatClock('2026-10-26T10:42:00Z', NOW)!
    const earlier = formatClock('2026-10-20T10:42:00Z', NOW)!
    expect(today).toMatch(/\d{1,2}:\d{2}/)
    expect(earlier.length).toBeGreaterThan(today.length)
    expect(earlier).toContain(today)
  })

  it('is null for no stamp or a torn one', () => {
    expect(formatClock(null, NOW)).toBeNull()
    expect(formatClock('yesterday', NOW)).toBeNull()
  })
})

describe('runCrumbLabel', () => {
  it('joins the time, the tag and the branch, leaving out what the run has none of', () => {
    const time = formatClock('2026-10-26T10:42:00Z', NOW)!
    expect(runCrumbLabel(run(), NOW)).toBe(`${time} · build-512 · main`)
    expect(runCrumbLabel(run({ tag: null }), NOW)).toBe(`${time} · main`)
    expect(runCrumbLabel(run({ tag: null, branch: null }), NOW)).toBe(time)
  })
})

describe('the trail', () => {
  it('links the run segment from a page below the run, and not from the run page itself', () => {
    expect(runCrumb(run(), false).to).toBe('/runs/run-1')
    expect(runCrumb(run(), true).to).toBeUndefined()
    expect(runCrumb(run(), true).title).toContain('/code/wallets')
  })

  it('the feature segment lands on the feature’s section of the run page', () => {
    expect(featureCrumb('run-1', 'Wallet', false).to).toEqual({ path: '/runs/run-1', hash: '#feature:Wallet' })
    expect(featureCrumb('run-1', '', false).label).toBe('(no feature)')
    expect(scenarioCrumb('credit').to).toBeUndefined()
  })
})
