import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useRunsStore } from '../runs-store'

/**
 * Issue #195 — the supervisor's forwarded per-test stream, the only per-test progress a run
 * whose workers are not Bobcat runners produces. These cases are the port of the server-side
 * ForeignTestProgressTests, event for event, so the two folds cannot drift.
 */
const RUN = '9a1f1a1e-0000-0000-0000-000000000195'

type Store = ReturnType<typeof useRunsStore>

function supervisedRun(store: Store, totalScenarios = 3) {
  store.handleRunStarted({
    runId: RUN,
    suite: 'ServiceTests',
    repository: '/repo',
    branch: 'main',
    mode: 'supervised',
    startedAt: '2026-09-01T10:00:00Z',
    totalScenarios,
  })
}

function testStarted(store: Store, uid: string, at: string, lane: number | null = 0) {
  store.handleTestStarted({ runId: RUN, uid, displayName: uid, lane, at })
}

function testFinished(store: Store, uid: string, state: string, at: string, durationMs: number | null = 10) {
  store.handleTestFinished({ runId: RUN, uid, displayName: uid, state, durationMs, lane: 0, at })
}

function scenarios(store: Store) {
  return Object.values(store.runById(RUN)!.scenarios)
}

function finishedCount(store: Store) {
  return scenarios(store).filter((s) => s.outcome !== null).length
}

describe('foreign per-test progress', () => {
  let store: Store

  beforeEach(() => {
    setActivePinia(createPinia())
    store = useRunsStore()
  })

  it('makes a forwarded verdict count as a finished scenario', () => {
    supervisedRun(store)
    testStarted(store, 'Acme.OrderTests.pays', '2026-09-01T10:00:01Z')
    testFinished(store, 'Acme.OrderTests.pays', 'Passed', '2026-09-01T10:00:03Z', 120)

    const scenario = scenarios(store)[0]
    expect(scenario.scenario).toBe('Acme.OrderTests.pays')
    expect(scenario.outcome).toBe('CleanPass')
    expect(scenario.state).toBe('Passed')
    expect(scenario.durationMs).toBe(120)
    expect(scenario.status).toBe('passed')
    expect(finishedCount(store)).toBe(1)
  })

  it('moves the run card that used to sit at zero for the whole run', () => {
    supervisedRun(store, 3)
    testFinished(store, 'A.one', 'Passed', '2026-09-01T10:00:03Z')
    testFinished(store, 'A.two', 'Failed', '2026-09-01T10:00:04Z')
    testStarted(store, 'A.three', '2026-09-01T10:00:05Z')

    expect(store.progressOf(store.runById(RUN)!)).toBeCloseTo(2 / 3)
  })

  it('never invents a feature for a test that is not a spec', () => {
    supervisedRun(store)
    testFinished(store, 'Acme.OrderTests.pays', 'Passed', '2026-09-01T10:00:03Z')

    expect(scenarios(store)[0].feature).toBe('')
  })

  it.each([
    ['Passed', 'CleanPass', 'passed'],
    ['Skipped', 'CleanPass', 'passed'],
    ['Failed', 'Failed', 'failed'],
    ['Error', 'Failed', 'failed'],
    ['Timeout', 'Failed', 'failed'],
    ['Cancelled', 'Failed', 'failed'],
    // A framework word this build has never met still finished; not counting it would stall
    // the progress bar for the whole run.
    ['SomethingNewInMtp', 'Failed', 'failed'],
  ])('maps the framework word %s onto %s', (state, outcome, status) => {
    supervisedRun(store)
    testFinished(store, 't', state, '2026-09-01T10:00:03Z')

    expect(scenarios(store)[0].outcome).toBe(outcome)
    expect(scenarios(store)[0].status).toBe(status)
    // The framework's own word survives beside the mapping.
    expect(scenarios(store)[0].state).toBe(state)
  })

  it('leaves an unmeasured duration null rather than zero', () => {
    supervisedRun(store)
    testFinished(store, 't', 'Passed', '2026-09-01T10:00:03Z', null)

    expect(scenarios(store)[0].durationMs).toBeNull()
  })

  // A supervised Bobcat suite puts BOTH streams on the wire, and the fold keeps one — in
  // either arrival order.

  it("lets the worker's own scenario win when the forwarded verdict arrives after it", () => {
    supervisedRun(store)
    store.handleScenarioStarted({
      runId: RUN,
      uid: 'Calc/adds',
      feature: 'Calc',
      scenario: 'adds',
      attempt: 1,
      at: '2026-09-01T10:00:01Z',
    })
    store.handleScenarioFinished({
      runId: RUN,
      uid: 'Calc/adds',
      outcome: 'PassOnRetry',
      attempts: 2,
      durationMs: 500,
      errorMessage: 'flaked once',
    })
    testFinished(store, 'Calc/adds', 'Passed', '2026-09-01T10:00:03Z', 480)

    const scenario = scenarios(store)[0]
    expect(scenario.outcome).toBe('PassOnRetry')
    expect(scenario.status).toBe('passed-on-retry')
    expect(scenario.durationMs).toBe(500)
    expect(scenario.errorMessage).toBe('flaked once')
    expect(scenario.state).toBeNull()
    expect(finishedCount(store)).toBe(1)
  })

  it("lets the worker's own scenario win when the forwarded verdict arrives before it", () => {
    supervisedRun(store)
    testFinished(store, 'Calc/adds', 'Passed', '2026-09-01T10:00:03Z', 480)
    store.handleScenarioStarted({
      runId: RUN,
      uid: 'Calc/adds',
      feature: 'Calc',
      scenario: 'adds',
      attempt: 1,
      at: '2026-09-01T10:00:01Z',
    })
    store.handleScenarioFinished({
      runId: RUN,
      uid: 'Calc/adds',
      outcome: 'PassOnRetry',
      attempts: 2,
      durationMs: 500,
      errorMessage: 'flaked once',
    })

    const scenario = scenarios(store)[0]
    expect(scenario.feature).toBe('Calc')
    expect(scenario.scenario).toBe('adds')
    expect(scenario.outcome).toBe('PassOnRetry')
    // One test, one card — the two streams never double-count the same uid.
    expect(finishedCount(store)).toBe(1)
  })

  it('does not let a forwarded start un-finish a scenario the worker owns', () => {
    supervisedRun(store)
    store.handleScenarioStarted({
      runId: RUN,
      uid: 'Calc/adds',
      feature: 'Calc',
      scenario: 'adds',
      attempt: 1,
      at: '2026-09-01T10:00:01Z',
    })
    store.handleScenarioFinished({
      runId: RUN,
      uid: 'Calc/adds',
      outcome: 'CleanPass',
      attempts: 1,
      durationMs: 20,
      errorMessage: null,
    })
    testStarted(store, 'Calc/adds', '2026-09-01T10:00:05Z')

    expect(finishedCount(store)).toBe(1)
  })

  it('ignores a replayed start older than the verdict it already has', () => {
    supervisedRun(store)
    testFinished(store, 't', 'Passed', '2026-09-01T10:00:03Z')
    testStarted(store, 't', '2026-09-01T10:00:01Z')

    expect(finishedCount(store)).toBe(1)
  })

  it('reopens a foreign test for a genuinely newer start', () => {
    supervisedRun(store)
    testFinished(store, 't', 'Failed', '2026-09-01T10:00:03Z')
    store.handleRetryScheduled({
      runId: RUN,
      uid: 't',
      nextAttempt: 2,
      disposition: 'RetryInFreshProcess',
      reason: 'flaky broker',
    })
    testStarted(store, 't', '2026-09-01T10:00:09Z', null)

    expect(scenarios(store)[0].outcome).toBeNull()
    expect(finishedCount(store)).toBe(0)
  })
})
