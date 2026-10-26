import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useRunsStore, type RunState } from '@/stores/runs-store'
import type { RunnerView } from '@/lib/runners'
import {
  commandLineOf,
  countScenarios,
  etaMs,
  formatElapsed,
  formatEta,
  liveProgressOf,
  plannedHintOf,
  plannedTotalOf,
  previousDurationsOf,
  toneOf,
} from '../liveProgress'

/**
 * bobcat#786 — a live run's overall progress: done of planned, passed and failed so far, the colour
 * rule and an ETA from the suite's previous runs. One computation for the suite row and the status
 * strip. A 3,000-test suite across six lanes used to show a neutral bar with no position for twenty
 * minutes.
 */
type Store = ReturnType<typeof useRunsStore>

function startRun(store: Store, runId: string, startedAt: string, totalScenarios: number | null, suite = 'Big.Suite'): RunState {
  store.handleRunStarted({ runId, suite, repository: '/repo', branch: 'main', mode: 'supervised', startedAt, totalScenarios })
  return store.runById(runId)!
}

function finish(store: Store, runId: string, finishedAt: string, passed: number, failed = 0) {
  store.handleRunFinished({ runId, exitCode: failed > 0 ? 1 : 0, passed, failed, passedOnRetry: 0, indeterminate: 0, finishedAt })
}

function scenario(store: Store, runId: string, uid: string, outcome: 'CleanPass' | 'Failed' | 'PassOnRetry' | null) {
  store.handleScenarioStarted({ runId, uid, feature: 'F', scenario: uid, attempt: 1, at: '2026-10-26T12:00:00Z' })
  if (outcome) store.handleScenarioFinished({ runId, uid, outcome, attempts: outcome === 'PassOnRetry' ? 2 : 1, durationMs: 10, errorMessage: null })
}

describe('the colour rule', () => {
  it('is green while everything passes, red at the first failure, amber when only retries have happened', () => {
    expect(toneOf({ failed: 0, retried: 0 })).toBe('green')
    expect(toneOf({ failed: 0, retried: 1 })).toBe('amber')
    expect(toneOf({ failed: 1, retried: 3 })).toBe('red')
  })
})

describe('counting a live run', () => {
  let store: Store
  beforeEach(() => {
    setActivePinia(createPinia())
    store = useRunsStore()
  })

  it('counts verdicts so far, retries as their own signal, and what is in flight', () => {
    const run = startRun(store, 'r', '2026-10-26T12:00:00Z', 10)
    scenario(store, 'r', 'a', 'CleanPass')
    scenario(store, 'r', 'b', 'Failed')
    scenario(store, 'r', 'c', 'PassOnRetry')
    scenario(store, 'r', 'd', null)
    store.handleRetryScheduled({ runId: 'r', uid: 'e', nextAttempt: 2, disposition: 'RetryInProcess', reason: 'flaky' })

    expect(countScenarios(run)).toEqual({ done: 3, passed: 2, failed: 1, retried: 2, running: 1 })
    expect(toneOf(countScenarios(run))).toBe('red')
  })

  it('reads the planned total from the run, then the heartbeat, then the runner, then the last run', () => {
    const declared = startRun(store, 'declared', '2026-10-26T12:00:00Z', 40)
    expect(plannedTotalOf(declared, 7, [])).toBe(40)

    const foreign = startRun(store, 'foreign', '2026-10-26T12:00:00Z', null)
    store.handleRunProgress({
      runId: 'foreign', elapsedMs: 1, completed: 3, total: 25, inFlight: 1, longestRunningUid: null,
      longestRunningDisplayName: null, longestRunningMs: null, peakWorkerRssBytes: null, at: '2026-10-26T12:00:01Z',
    })
    expect(plannedTotalOf(foreign, 7, [])).toBe(25)

    const hinted = startRun(store, 'hinted', '2026-10-26T12:00:00Z', null)
    expect(plannedTotalOf(hinted, 7, [])).toBe(7)

    const previous = startRun(store, 'previous', '2026-10-26T11:00:00Z', null)
    finish(store, 'previous', '2026-10-26T11:02:00Z', 18, 2)
    expect(plannedTotalOf(hinted, null, [previous])).toBe(20)
    expect(plannedTotalOf(hinted, null, [])).toBeNull()
  })

  it('takes the planned hint from the command that caused the run before the whole runner', () => {
    const runners: RunnerView[] = [{
      runnerId: 'r1', repository: '/repo', repositorySlug: null, branch: 'main', suite: 'Big.Suite', lane: 'projected',
      modes: ['cold'], specs: 22, connected: true, status: null, statusDetail: null, registeredAt: '', lastSeenAt: '',
      commands: [{
        commandId: 'c1', kind: 'run', specs: ['F/a', 'F/b'], mode: 'cold', state: 'accepted', reason: null,
        requestedBy: 'bobcat-ui', requestedAt: '', acknowledgedAt: null, runId: 'commanded',
      }],
    }]
    const commanded = startRun(store, 'commanded', '2026-10-26T12:00:00Z', null)
    const whole = startRun(store, 'whole', '2026-10-26T12:00:00Z', null)
    const elsewhere = startRun(store, 'elsewhere', '2026-10-26T12:00:00Z', null, 'Other.Suite')

    expect(plannedHintOf(commanded, runners)).toBe(2)
    expect(plannedHintOf(whole, runners)).toBe(22)
    expect(plannedHintOf(elsewhere, runners)).toBeNull()
    expect(plannedHintOf(whole, null)).toBeNull()

    scenario(store, 'commanded', 'F/a', 'CleanPass')
    expect(commandLineOf(commanded, runners)).toBe('Run 2 in feature F · cold · from bobcat-ui · 1 finished of 2 specs selected')
    expect(commandLineOf(whole, runners)).toBeNull()
  })

  it('puts it all together as of now', () => {
    const previous = startRun(store, 'previous', '2026-10-26T11:00:00Z', 4)
    finish(store, 'previous', '2026-10-26T11:04:00Z', 4)
    const run = startRun(store, 'r', '2026-10-26T12:00:00Z', 4)
    scenario(store, 'r', 'a', 'CleanPass')

    const progress = liveProgressOf(run, Date.parse('2026-10-26T12:01:00Z'), null, [previous])
    expect(progress.planned).toBe(4)
    expect(progress.fraction).toBe(0.25)
    expect(progress.tone).toBe('green')
    expect(progress.elapsedMs).toBe(60_000)
    // The previous run took four minutes; one has elapsed.
    expect(progress.etaMs).toBe(180_000)
  })
})

describe('the estimate', () => {
  it('is the median of the previous runs less the elapsed time, never below zero', () => {
    expect(etaMs(60_000, 1, 10, [240_000, 200_000, 900_000])).toBe(180_000)
    expect(etaMs(300_000, 1, 10, [240_000])).toBe(0)
    // Two previous runs: the mean of the middle pair.
    expect(etaMs(0, 0, 10, [100_000, 300_000])).toBe(200_000)
  })

  it('extrapolates from the run\'s own pace when the suite has no history, once anything has finished', () => {
    expect(etaMs(20_000, 2, 10, [])).toBe(80_000)
    expect(etaMs(20_000, 0, 10, [])).toBeNull()
    expect(etaMs(20_000, 2, null, [])).toBeNull()
  })

  it('takes durations only from runs that finished', () => {
    setActivePinia(createPinia())
    const store = useRunsStore()
    const done = startRun(store, 'done', '2026-10-26T11:00:00Z', 1)
    finish(store, 'done', '2026-10-26T11:01:30Z', 1)
    const orphan = startRun(store, 'orphan', '2026-10-26T10:00:00Z', 1)
    store.markOrphaned('orphan')
    expect(previousDurationsOf([done, orphan])).toEqual([90_000])
  })

  it('reads coarsely', () => {
    expect(formatEta(null)).toBeNull()
    expect(formatEta(2_000)).toBe('any moment')
    expect(formatEta(43_000)).toBe('about 45s')
    expect(formatEta(180_000)).toBe('about 3m')
    expect(formatEta(3_900_000)).toBe('about 1h 5m')
    expect(formatElapsed(42_500)).toBe('42s')
    expect(formatElapsed(65_000)).toBe('1m05s')
    expect(formatElapsed(3_660_000)).toBe('1h01m')
  })
})
