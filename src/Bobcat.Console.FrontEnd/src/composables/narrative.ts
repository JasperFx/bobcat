import type { DeclaredStepInfo } from '@/messages/monitor-events'
import type { ScenarioState, StepState } from '@/stores/runs-store'

/**
 * Issue #304 — a marker-comment scenario, rendered as what it SAID it would do with what
 * actually happened underneath each sentence.
 *
 * The two are different things and this file keeps them different. A declared step is the test
 * author's own sentence, read from a comment at compile time; it has no verdict and never earns
 * one here. A recorded step is a `[BobcatStep]` helper that ran, with a real duration and a real
 * failure, and it says which sentence it ran under — a number the GENERATOR computed from the
 * call site's line, never something inferred at runtime from a stack or a clock.
 *
 * So a declared row reports its children and nothing else. **A row with no children stays
 * blank**: nothing observed it, and a duration invented from its neighbours would be exactly the
 * kind of claim `docs/marker-steps.md` promises this feature does not make.
 */
export type NarrativeStatus = 'declared' | 'running' | 'passed' | 'failed'

export interface NarrativeRow {
  /** 1-based position in the declared narrative — the number a step attributes itself to. */
  number: number
  keyword: string
  text: string
  status: NarrativeStatus
  /**
   * Milliseconds of observed work inside the row: the sum of its children's own durations, not
   * a wall clock across the region. Null when nothing was observed, and when a child is still
   * running.
   */
  observedMs: number | null
  errorMessage: string | null
  steps: StepState[]
}

export interface Narrative {
  rows: NarrativeRow[]
  /**
   * Recorded steps attributed to no declared row — a helper called before the first marker
   * comment, or a scenario that declares nothing at all. Rendered on their own rather than
   * folded into a neighbouring sentence, because "which sentence" is a question that had no
   * answer, and picking one would be a guess.
   */
  outside: StepState[]
}

function statusOf(steps: StepState[]): NarrativeStatus {
  if (steps.length === 0) return 'declared'
  if (steps.some((s) => s.status === 'failed')) return 'failed'
  if (steps.some((s) => s.status === 'running')) return 'running'
  return 'passed'
}

function observedMsOf(steps: StepState[]): number | null {
  if (steps.length === 0) return null
  if (steps.some((s) => s.durationMs === null)) return null
  return steps.reduce((total, s) => total + (s.durationMs ?? 0), 0)
}

export function narrativeFor(scenario: {
  declaredSteps: DeclaredStepInfo[]
  steps: StepState[]
}): Narrative {
  const rows: NarrativeRow[] = scenario.declaredSteps.map((declared, index) => {
    const steps = scenario.steps.filter((s) => s.declaredStepNumber === index + 1)
    return {
      number: index + 1,
      keyword: declared.keyword,
      text: declared.text,
      status: statusOf(steps),
      observedMs: observedMsOf(steps),
      errorMessage: steps.find((s) => s.errorMessage)?.errorMessage ?? null,
      steps,
    }
  })

  // A number past the end of the narrative belongs to nothing rather than to the last row: the
  // runtime already refuses to attribute one, and a viewer reading an archived run from an older
  // publisher should reach the same answer rather than a different one.
  const outside = scenario.steps.filter(
    (s) => s.declaredStepNumber === null || s.declaredStepNumber > rows.length
  )

  return { rows, outside }
}

/** True when there is a narrative to draw at all. */
export function hasNarrative(scenario: Pick<ScenarioState, 'declaredSteps'>): boolean {
  return scenario.declaredSteps.length > 0
}
