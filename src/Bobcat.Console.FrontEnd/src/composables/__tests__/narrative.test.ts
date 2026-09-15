import { describe, expect, it } from 'vitest'
import { narrativeFor } from '../narrative'
import type { StepState } from '@/stores/runs-store'

/**
 * Issue #304 — what a marker-comment scenario looks like once the narrative and the recorded
 * work are put back together. The rule under test throughout: a declared row reports its
 * children and never anything else, so a row nothing ran under stays blank rather than
 * borrowing a number from its neighbours.
 */
function step(overrides: Partial<StepState> & { stepId: string }): StepState {
  return {
    kind: 'Given',
    text: 'a helper ran',
    status: 'passed',
    durationMs: 10,
    errorMessage: null,
    stepNumber: null,
    scenarioElapsedMs: null,
    progress: null,
    declaredStepNumber: null,
    ...overrides,
  }
}

const declared = [
  { keyword: 'Given', text: 'the events are published' },
  { keyword: 'When', text: 'the daemon is running' },
  { keyword: 'Then', text: 'every aggregate matches' },
]

describe('narrativeFor', () => {
  it('puts each recorded step under the sentence it ran beneath', () => {
    const narrative = narrativeFor({
      declaredSteps: declared,
      steps: [
        step({ stepId: 's1', declaredStepNumber: 1 }),
        step({ stepId: 's2', declaredStepNumber: 1 }),
        step({ stepId: 's3', declaredStepNumber: 3 }),
      ],
    })

    expect(narrative.rows.map((r) => r.steps.map((s) => s.stepId))).toEqual([
      ['s1', 's2'],
      [],
      ['s3'],
    ])
    expect(narrative.rows.map((r) => `${r.keyword} ${r.text}`)).toEqual([
      'Given the events are published',
      'When the daemon is running',
      'Then every aggregate matches',
    ])
  })

  it('leaves a sentence nothing ran under with no verdict and no duration', () => {
    // The honest limit, and the whole reason this is not derived from timing: nothing observed
    // that step, so the row says so instead of claiming a number.
    const row = narrativeFor({ declaredSteps: declared, steps: [] }).rows[1]!

    expect(row.status).toBe('declared')
    expect(row.observedMs).toBeNull()
    expect(row.errorMessage).toBeNull()
  })

  it('reports observed work as the sum of its children, not a wall clock', () => {
    const row = narrativeFor({
      declaredSteps: declared,
      steps: [
        step({ stepId: 's1', declaredStepNumber: 1, durationMs: 10 }),
        step({ stepId: 's2', declaredStepNumber: 1, durationMs: 32 }),
      ],
    }).rows[0]!

    expect(row.observedMs).toBe(42)
    expect(row.status).toBe('passed')
  })

  it('withholds a duration while a child is still running, and says the row is running', () => {
    const row = narrativeFor({
      declaredSteps: declared,
      steps: [
        step({ stepId: 's1', declaredStepNumber: 1, durationMs: 10 }),
        step({ stepId: 's2', declaredStepNumber: 1, status: 'running', durationMs: null }),
      ],
    }).rows[0]!

    expect(row.status).toBe('running')
    expect(row.observedMs).toBeNull()
  })

  it('fails the sentence its failing step ran under, and carries the message up', () => {
    const row = narrativeFor({
      declaredSteps: declared,
      steps: [
        step({ stepId: 's1', declaredStepNumber: 2, status: 'failed', errorMessage: 'no shard' }),
      ],
    }).rows[1]!

    expect(row.status).toBe('failed')
    expect(row.errorMessage).toBe('no shard')
  })

  it('keeps a step that belongs to no sentence outside the narrative', () => {
    // A helper called before the first marker comment. Folding it into the first sentence would
    // put work under something the author had not written yet.
    const narrative = narrativeFor({
      declaredSteps: declared,
      steps: [step({ stepId: 's0', declaredStepNumber: null })],
    })

    expect(narrative.outside.map((s) => s.stepId)).toEqual(['s0'])
    expect(narrative.rows.every((r) => r.steps.length === 0)).toBe(true)
  })

  it('treats an index past the end of the narrative as outside it', () => {
    // An archived run from a publisher whose comments have since changed. The runtime refuses
    // the same attribution; a viewer reading history must not reach a different answer.
    const narrative = narrativeFor({
      declaredSteps: declared,
      steps: [step({ stepId: 's9', declaredStepNumber: 9 })],
    })

    expect(narrative.outside.map((s) => s.stepId)).toEqual(['s9'])
  })

  it('renders every other authoring style exactly as before — one flat list, no rows', () => {
    const narrative = narrativeFor({
      declaredSteps: [],
      steps: [step({ stepId: 's1' }), step({ stepId: 's2' })],
    })

    expect(narrative.rows).toEqual([])
    expect(narrative.outside.map((s) => s.stepId)).toEqual(['s1', 's2'])
  })
})
