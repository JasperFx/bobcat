import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useRunsStore } from '../runs-store'

const RUN = '9a1f1a1e-0000-0000-0000-000000000001'

function startRun(store: ReturnType<typeof useRunsStore>, totalScenarios: number | null = 2) {
  store.handleRunStarted({
    runId: RUN,
    suite: 'Wolverine PersistenceTests',
    repository: '/Users/dev/code/wolverine',
    branch: 'main',
    mode: 'supervised',
    startedAt: '2026-07-31T10:00:00Z',
    totalScenarios,
  })
}

describe('runs-store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('registers a run from run_started', () => {
    const store = useRunsStore()
    startRun(store)

    expect(store.allRuns).toHaveLength(1)
    const run = store.runById(RUN)!
    expect(run.suite).toBe('Wolverine PersistenceTests')
    expect(run.repository).toBe('/Users/dev/code/wolverine')
    expect(run.branch).toBe('main')
    expect(run.finished).toBe(false)
    expect(store.activeRuns).toHaveLength(1)
  })

  it('tracks a clean scenario through steps to completion', () => {
    const store = useRunsStore()
    startRun(store)

    const uid = 'Calculator/Adds two numbers'
    store.handleScenarioStarted({
      runId: RUN,
      uid,
      feature: 'Calculator',
      scenario: 'Adds two numbers',
      attempt: 1,
      at: '2026-07-31T10:00:01Z',
    })
    store.handleStepStarted({ runId: RUN, uid, stepId: 's1', kind: 'Given', text: 'a calculator' })
    store.handleStepFinished({
      runId: RUN,
      uid,
      stepId: 's1',
      status: 'ok',
      durationMs: 12,
      errorMessage: null,
    })
    store.handleScenarioFinished({
      runId: RUN,
      uid,
      outcome: 'CleanPass',
      attempts: 1,
      durationMs: 40,
      errorMessage: null,
    })

    const scenario = store.runById(RUN)!.scenarios[uid]!
    expect(scenario.status).toBe('passed')
    expect(scenario.steps).toHaveLength(1)
    expect(scenario.steps[0]!.status).toBe('passed')
    expect(scenario.steps[0]!.durationMs).toBe(12)
    // An evidence-blind publisher (no touchedTypes, no at) leaves the defaults standing.
    expect(scenario.touchedTypes).toEqual([])
    expect(scenario.finishedAt).toBeNull()
  })

  it('scenario_finished folds the touched-type evidence and its stamp', () => {
    const store = useRunsStore()
    const uid = 'Credit Wallet/happy path'
    store.handleScenarioStarted({
      runId: RUN,
      uid,
      feature: 'Credit Wallet',
      scenario: 'happy path',
      attempt: 1,
      at: '2026-08-24T10:00:01Z',
    })
    store.handleScenarioFinished({
      runId: RUN,
      uid,
      outcome: 'CleanPass',
      attempts: 1,
      durationMs: 120,
      errorMessage: null,
      touchedTypes: [
        { name: 'CreditWallet', fullName: 'Wallets.CreditWallet', assemblyName: 'Wallets' },
        { name: 'WalletCredited', fullName: 'Wallets.WalletCredited', assemblyName: 'Wallets' },
      ],
      at: '2026-08-24T10:00:03Z',
    })

    const scenario = store.runById(RUN)!.scenarios[uid]!
    expect(scenario.touchedTypes.map((t) => t.fullName)).toEqual([
      'Wallets.CreditWallet',
      'Wallets.WalletCredited',
    ])
    expect(scenario.finishedAt).toBe('2026-08-24T10:00:03Z')
  })

  it('a retry clears the step list for the fresh attempt and lands on passed-on-retry', () => {
    const store = useRunsStore()
    startRun(store)

    const uid = 'Orders/Broker warms up'
    store.handleScenarioStarted({
      runId: RUN,
      uid,
      feature: 'Orders',
      scenario: 'Broker warms up',
      attempt: 1,
      at: '2026-07-31T10:00:01Z',
    })
    store.handleStepStarted({ runId: RUN, uid, stepId: 's1', kind: 'When', text: 'the broker is asked' })
    store.handleStepFinished({
      runId: RUN,
      uid,
      stepId: 's1',
      status: 'error',
      durationMs: 5000,
      errorMessage: 'TimeoutException',
    })
    store.handleRetryScheduled({
      runId: RUN,
      uid,
      nextAttempt: 2,
      disposition: 'RetryInProcess',
      reason: 'the broker is slow to warm up',
    })

    let scenario = store.runById(RUN)!.scenarios[uid]!
    expect(scenario.status).toBe('retry-scheduled')
    expect(scenario.retryReason).toBe('the broker is slow to warm up')

    // Second attempt: fresh bracket, fresh steps.
    store.handleScenarioStarted({
      runId: RUN,
      uid,
      feature: 'Orders',
      scenario: 'Broker warms up',
      attempt: 2,
      at: '2026-07-31T10:00:07Z',
    })
    scenario = store.runById(RUN)!.scenarios[uid]!
    expect(scenario.steps).toHaveLength(0)
    expect(scenario.attempt).toBe(2)
    expect(scenario.status).toBe('running')

    store.handleScenarioFinished({
      runId: RUN,
      uid,
      outcome: 'PassOnRetry',
      attempts: 2,
      durationMs: 900,
      errorMessage: null,
    })
    scenario = store.runById(RUN)!.scenarios[uid]!
    expect(scenario.status).toBe('passed-on-retry')
    expect(scenario.attempts).toBe(2)
  })

  it('a supervised retry is numbered by the scheduled attempt, not by the fresh worker', () => {
    // The worker running a supervised retry counts from one — its attempt tracking belongs to
    // a BobcatRunner, and the MTP host builds a fresh one per run request. Only the supervisor
    // knows this start is a second try, and retry_scheduled is how it says so.
    const store = useRunsStore()
    startRun(store)

    const uid = 'Orders/Broker warms up'
    const start = (attempt: number) =>
      store.handleScenarioStarted({
        runId: RUN,
        uid,
        feature: 'Orders',
        scenario: 'Broker warms up',
        attempt,
        at: '2026-07-31T10:00:01Z',
      })

    start(1)
    store.handleRetryScheduled({
      runId: RUN,
      uid,
      nextAttempt: 2,
      disposition: 'RetryInFreshProcess',
      reason: 'the broker is slow to warm up',
    })

    // A brand-new process: it has never seen this test, so it says attempt 1.
    start(1)
    expect(store.runById(RUN)!.scenarios[uid]!.attempt).toBe(2)

    // Consumed by the start it belonged to — a later start is not promoted again.
    start(1)
    expect(store.runById(RUN)!.scenarios[uid]!.attempt).toBe(2)

    store.handleScenarioFinished({
      runId: RUN,
      uid,
      outcome: 'PassOnRetry',
      // The worker reports its own count; a total can never be fewer than what we watched start.
      attempts: 1,
      durationMs: 900,
      errorMessage: null,
    })
    expect(store.runById(RUN)!.scenarios[uid]!.attempts).toBe(2)
  })

  it('computes progress from finished scenarios over the announced total', () => {
    const store = useRunsStore()
    startRun(store, 4)

    const finish = (uid: string) => {
      store.handleScenarioStarted({
        runId: RUN,
        uid,
        feature: 'F',
        scenario: uid,
        attempt: 1,
        at: '2026-07-31T10:00:01Z',
      })
      store.handleScenarioFinished({
        runId: RUN,
        uid,
        outcome: 'CleanPass',
        attempts: 1,
        durationMs: 10,
        errorMessage: null,
      })
    }
    finish('F/a')
    finish('F/b')

    const run = store.runById(RUN)!
    expect(store.progressOf(run)).toBeCloseTo(0.5)
  })

  it('progress is null when the total is unknown', () => {
    const store = useRunsStore()
    startRun(store, null)
    expect(store.progressOf(store.runById(RUN)!)).toBeNull()
  })

  it('tolerates events arriving before run_started by synthesizing a shell run', () => {
    const store = useRunsStore()
    store.handleScenarioStarted({
      runId: RUN,
      uid: 'Late/Arrival',
      feature: 'Late',
      scenario: 'Arrival',
      attempt: 1,
      at: '2026-07-31T10:00:00Z',
    })

    expect(store.allRuns).toHaveLength(1)
    expect(store.runById(RUN)!.scenarios['Late/Arrival']).toBeDefined()

    // Metadata backfills when run_started shows up late.
    startRun(store)
    expect(store.runById(RUN)!.suite).toBe('Wolverine PersistenceTests')
  })

  it('run_finished records counts and exit code, and eject removes the run', () => {
    const store = useRunsStore()
    startRun(store)
    store.handleRunFinished({
      runId: RUN,
      exitCode: 0,
      passed: 10,
      failed: 0,
      passedOnRetry: 1,
      indeterminate: 0,
      finishedAt: '2026-07-31T10:05:00Z',
    })

    const run = store.runById(RUN)!
    expect(run.finished).toBe(true)
    expect(run.exitCode).toBe(0)
    expect(run.counts).toEqual({ passed: 10, failed: 0, passedOnRetry: 1, indeterminate: 0 })
    expect(store.finishedRuns).toHaveLength(1)

    store.removeRun(RUN)
    expect(store.allRuns).toHaveLength(0)
  })
})
