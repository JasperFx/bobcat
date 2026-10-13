import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useRunsStore } from '../runs-store'

/**
 * stoat#58–#60, #62 — a step's report folded onto the store: cells, logs, diagnostics, the plan
 * and the exception. Mirrors the server fold (SpecificationReportProjectionTests), and holds to
 * the store's usual rules: assign whole, never append, so a hydration replay lands the same report.
 */
const RUN = '11111111-1111-1111-1111-111111111058'
const UID = 'Calculator/divides'

beforeEach(() => setActivePinia(createPinia()))

function started(store: ReturnType<typeof useRunsStore>) {
  store.handleScenarioStarted({
    runId: RUN,
    uid: UID,
    feature: 'Calculator',
    scenario: 'divides',
    attempt: 1,
    at: '2026-09-29T12:00:00Z',
    plannedSteps: [{ keyword: '', template: 'The value should be {value}', grammar: 'G.ValueIs', declaredStepNumber: null }],
  })
  store.handleStepStarted({
    runId: RUN,
    uid: UID,
    stepId: 'Check',
    kind: 'Then',
    text: 'the Sum should be 6',
    stepNumber: 1,
    plannedStepNumber: 1,
    values: [{ start: 18, length: 1 }],
  })
}

const step = (store: ReturnType<typeof useRunsStore>) => store.runById(RUN)!.scenarios[UID]!.steps[0]!

describe('runs-store — a step’s report', () => {
  it('folds the plan, the plan position and the input values', () => {
    const store = useRunsStore()
    started(store)

    expect(store.runById(RUN)!.scenarios[UID]!.plannedSteps.map((p) => p.keyword)).toEqual([''])
    expect(step(store).plannedStepNumber).toBe(1)
    expect(step(store).values).toEqual([{ start: 18, length: 1 }])
  })

  it('folds cells, logs and diagnostics from step_finished, and a replay lands the same report', () => {
    const store = useRunsStore()
    started(store)
    const finished = {
      runId: RUN,
      uid: UID,
      stepId: 'Check',
      status: 'failed',
      durationMs: 2,
      errorMessage: "Sum: expected '6', got '8'",
      cells: [
        { name: 'Sum', status: 'failed', expected: '6', actual: '8', note: null, rowIndex: -1, value: null, comparison: null },
        { name: 'Product', status: 'success', expected: '16', actual: '16', note: null, rowIndex: -1, value: null, comparison: null },
      ],
      logs: ['computed'],
      diagnostics: { x: '4' },
    }
    store.handleStepFinished(finished)
    store.handleStepFinished(finished)

    expect(step(store).cells).toHaveLength(2)
    expect(step(store).logs).toEqual(['computed'])
    expect(step(store).diagnostics).toEqual({ x: '4' })
    // A disagreement threw nothing: no exception, and the store does not invent one from the text.
    expect(step(store).exception).toBeNull()
  })

  it('keeps an exception apart from the message, with the filtered frames and the hidden count', () => {
    const store = useRunsStore()
    started(store)
    store.handleStepFinished({
      runId: RUN,
      uid: UID,
      stepId: 'Check',
      status: 'error',
      durationMs: 2,
      errorMessage: 'Attempted to divide by zero.',
      exceptionType: 'System.DivideByZeroException',
      stackTrace: 'raw',
      stackFrames: ['at A()'],
      hiddenStackFrames: 2,
    })

    expect(step(store).status).toBe('failed')
    expect(step(store).exception).toEqual({
      type: 'System.DivideByZeroException',
      message: 'Attempted to divide by zero.',
      frames: ['at A()'],
      hiddenFrames: 2,
      stackTrace: 'raw',
    })
  })

  it('leaves every report field empty from an older publisher', () => {
    const store = useRunsStore()
    started(store)
    store.handleStepFinished({ runId: RUN, uid: UID, stepId: 'Check', status: 'ok', durationMs: 1, errorMessage: null })

    expect(step(store)).toMatchObject({ cells: [], columns: [], logs: [], diagnostics: {}, exception: null })
  })
})

describe('runs-store — cells so far, while a step runs (stoat#62)', () => {
  const progress = (cells?: { name: string; status: string }[]) => ({
    runId: RUN,
    uid: UID,
    stepId: 'Check',
    message: null,
    row: null,
    totalRows: null,
    elapsedMs: 100,
    cells: cells?.map((c) => ({ ...c, expected: null, actual: null, note: null, rowIndex: -1, value: null, comparison: null })),
  })

  it('shows the latest set, and keeps it through an update that says nothing about cells', () => {
    const store = useRunsStore()
    started(store)

    store.handleStepProgress(progress([{ name: 'a', status: 'success' }]))
    store.handleStepProgress(progress([{ name: 'a', status: 'success' }, { name: 'b', status: 'failed' }]))
    expect(step(store).progress!.cells.map((c) => c.name)).toEqual(['a', 'b'])

    store.handleStepProgress(progress())
    expect(step(store).progress!.cells.map((c) => c.name)).toEqual(['a', 'b'])
  })

  it('gives way to the step’s own cells when it finishes', () => {
    const store = useRunsStore()
    started(store)
    store.handleStepProgress(progress([{ name: 'a', status: 'success' }]))
    store.handleStepFinished({
      runId: RUN,
      uid: UID,
      stepId: 'Check',
      status: 'success',
      durationMs: 5,
      errorMessage: null,
      cells: [{ name: 'final', status: 'success', expected: '1', actual: '1', note: null, rowIndex: -1, value: null, comparison: null }],
    })

    expect(step(store).progress).toBeNull()
    expect(step(store).cells.map((c) => c.name)).toEqual(['final'])
  })
})
