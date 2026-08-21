import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useRunsStore } from '../runs-store'

/**
 * Issue #99 — the progress model for a scenario in flight, folded from recorded event
 * sequences: step n of N, row k of M for a [TableGrammar], and the [WaitFor] poll loop's
 * interim message with elapsed.
 */
const RUN = '9a1f1a1e-0000-0000-0000-000000000099'
const UID = 'Customers/Bulk import'

function startScenario(store: ReturnType<typeof useRunsStore>, totalSteps: number | null = 3) {
  store.handleRunStarted({
    runId: RUN,
    suite: 'Orders',
    repository: '/repo',
    branch: 'main',
    mode: 'in-process',
    startedAt: '2026-08-21T10:00:00Z',
    totalScenarios: 1,
  })
  store.handleScenarioStarted({
    runId: RUN,
    uid: UID,
    feature: 'Customers',
    scenario: 'Bulk import',
    attempt: 1,
    at: '2026-08-21T10:00:01Z',
    totalSteps,
  })
}

describe('runs-store step progress', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('records the step count from scenario_started and each step position', () => {
    const store = useRunsStore()
    startScenario(store, 3)

    store.handleStepStarted({
      runId: RUN,
      uid: UID,
      stepId: 's1',
      kind: 'Given',
      text: 'a clean database',
      stepNumber: 1,
      totalSteps: 3,
      scenarioElapsedMs: 2,
    })

    const scenario = store.runById(RUN)!.scenarios[UID]!
    expect(scenario.totalSteps).toBe(3)
    expect(scenario.steps[0]!.stepNumber).toBe(1)
    expect(scenario.steps[0]!.scenarioElapsedMs).toBe(2)
    expect(scenario.steps[0]!.progress).toBeNull()
  })

  it('falls back to the count on step_started when scenario_started had none', () => {
    // A publisher that predates totalSteps on scenario_started, or a scenario_started lost to
    // backpressure — the step still says how many there are.
    const store = useRunsStore()
    startScenario(store, null)

    store.handleStepStarted({
      runId: RUN,
      uid: UID,
      stepId: 's1',
      kind: 'Given',
      text: 'a clean database',
      stepNumber: 1,
      totalSteps: 4,
    })

    expect(store.runById(RUN)!.scenarios[UID]!.totalSteps).toBe(4)
  })

  it('an old publisher without the new fields leaves them null, not undefined', () => {
    const store = useRunsStore()
    store.handleScenarioStarted({
      runId: RUN,
      uid: UID,
      feature: 'Customers',
      scenario: 'Bulk import',
      attempt: 1,
      at: '2026-08-21T10:00:01Z',
    })
    store.handleStepStarted({ runId: RUN, uid: UID, stepId: 's1', kind: 'Given', text: 'a thing' })

    const scenario = store.runById(RUN)!.scenarios[UID]!
    expect(scenario.totalSteps).toBeNull()
    expect(scenario.steps[0]!.stepNumber).toBeNull()
    expect(scenario.steps[0]!.scenarioElapsedMs).toBeNull()
  })

  it('folds row progress onto the running step, latest wins, cleared on finish', () => {
    const store = useRunsStore()
    startScenario(store, 1)
    store.handleStepStarted({
      runId: RUN,
      uid: UID,
      stepId: 'grammar',
      kind: 'Given',
      text: 'the following customers exist',
      stepNumber: 1,
      totalSteps: 1,
      scenarioElapsedMs: 0,
    })

    store.handleStepProgress({ runId: RUN, uid: UID, stepId: 'grammar', message: null, row: 1, totalRows: 200, elapsedMs: 3 })
    store.handleStepProgress({ runId: RUN, uid: UID, stepId: 'grammar', message: null, row: 140, totalRows: 200, elapsedMs: 3100 })

    const step = store.runById(RUN)!.scenarios[UID]!.steps[0]!
    expect(step.progress).toEqual({ message: null, row: 140, totalRows: 200, elapsedMs: 3100 })

    store.handleStepFinished({
      runId: RUN,
      uid: UID,
      stepId: 'grammar',
      status: 'success',
      durationMs: 4400,
      errorMessage: null,
      scenarioElapsedMs: 4400,
    })
    expect(step.status).toBe('passed')
    expect(step.progress).toBeNull()
  })

  it('folds a wait-for poll message with elapsed', () => {
    const store = useRunsStore()
    startScenario(store, 2)
    store.handleStepStarted({
      runId: RUN,
      uid: UID,
      stepId: 'wait',
      kind: 'Then',
      text: 'the queue eventually drains',
      stepNumber: 2,
      totalSteps: 2,
      scenarioElapsedMs: 900,
    })
    store.handleStepProgress({
      runId: RUN,
      uid: UID,
      stepId: 'wait',
      message: 'waiting… (attempt 4, 800ms); last value 2',
      row: null,
      totalRows: null,
      elapsedMs: 800,
    })

    const step = store.runById(RUN)!.scenarios[UID]!.steps[0]!
    expect(step.progress?.message).toBe('waiting… (attempt 4, 800ms); last value 2')
    expect(step.progress?.row).toBeNull()
    expect(step.progress?.elapsedMs).toBe(800)
  })

  it('progress that outruns its step_started synthesizes a running step rather than vanishing', () => {
    const store = useRunsStore()
    startScenario(store, 1)

    store.handleStepProgress({ runId: RUN, uid: UID, stepId: 'grammar', message: null, row: 2, totalRows: 5, elapsedMs: 10 })

    const scenario = store.runById(RUN)!.scenarios[UID]!
    expect(scenario.steps).toHaveLength(1)
    expect(scenario.steps[0]!.status).toBe('running')
    expect(scenario.steps[0]!.progress?.row).toBe(2)
  })

  it('late progress replayed after step_finished is ignored', () => {
    // Hydration replays the archive over live state: a step_progress arriving after the
    // step_finished that already superseded it must not resurrect "row 3 of 5".
    const store = useRunsStore()
    startScenario(store, 1)
    store.handleStepStarted({ runId: RUN, uid: UID, stepId: 'grammar', kind: 'Given', text: 'rows', stepNumber: 1, totalSteps: 1 })
    store.handleStepFinished({ runId: RUN, uid: UID, stepId: 'grammar', status: 'success', durationMs: 5, errorMessage: null })
    store.handleStepProgress({ runId: RUN, uid: UID, stepId: 'grammar', message: null, row: 3, totalRows: 5, elapsedMs: 3 })

    expect(store.runById(RUN)!.scenarios[UID]!.steps[0]!.progress).toBeNull()
  })

  it('a retry resets the step list and takes the new attempt\'s count', () => {
    const store = useRunsStore()
    startScenario(store, 3)
    store.handleStepStarted({ runId: RUN, uid: UID, stepId: 's1', kind: 'Given', text: 'a', stepNumber: 1, totalSteps: 3 })
    store.handleStepProgress({ runId: RUN, uid: UID, stepId: 's1', message: 'waiting…', row: null, totalRows: null, elapsedMs: 1 })

    store.handleScenarioStarted({
      runId: RUN,
      uid: UID,
      feature: 'Customers',
      scenario: 'Bulk import',
      attempt: 2,
      at: '2026-08-21T10:00:05Z',
      totalSteps: 3,
    })

    const scenario = store.runById(RUN)!.scenarios[UID]!
    expect(scenario.steps).toHaveLength(0)
    expect(scenario.totalSteps).toBe(3)
    expect(scenario.attempt).toBe(2)
  })
})
