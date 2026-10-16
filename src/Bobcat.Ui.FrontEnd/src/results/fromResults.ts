import type { DeclaredStepInfo, PlannedStepInfo, StepCell } from '@/messages/monitor-events'
import type {
  BobcatResults,
  ResultsAttachment,
  ResultsCell,
  ResultsGrid,
  ResultsScenario,
  ResultsStep,
} from '@/messages/results-file'
import type { ScenarioStatus, StepAttachment, StepState } from '@/stores/runs-store'

/**
 * The HTML results page (bobcat#541) reads a run's results FILE, not the live wire, and renders it
 * through the board's own `SpecificationView` — so an archived scenario and a live one look the
 * same. This file is the one translation between the two shapes: the results file's scenario (what
 * `JsonRenderer` writes) into the board's `ScenarioState` subset that `specificationFor` reads.
 */

/** One results file as the page was handed it, with where its attachments are from the page. */
export interface ResultsPageFile {
  /**
   * The results file's directory relative to the page, ending in `/` (or `''` beside it): an
   * attachment's `path` is relative to that directory. Null when the directory was not kept, so
   * there are no files to link to.
   */
  attachmentBase: string | null
  results: BobcatResults
}

/** What the page is handed: every results file of the run, inlined by `bobcat results-html`. */
export interface ResultsPageData {
  files: ResultsPageFile[]
}

/** The id of the element the tool inlines the run's results into. */
export const RESULTS_ELEMENT_ID = 'bobcat-results'

/**
 * The inlined results (the text of the element named {@link RESULTS_ELEMENT_ID}), or an empty
 * page's. Never throws: a page that cannot read its data says so, rather than rendering nothing.
 */
export function readPageData(inlined: string | null | undefined): { data: ResultsPageData; error: string | null } {
  const text = inlined ?? ''
  if (text.trim().length === 0) return { data: { files: [] }, error: null }
  try {
    const parsed = JSON.parse(text) as Partial<ResultsPageData> | BobcatResults[]
    // A bare array of results files is accepted too: it is what a hand-made page would hold.
    if (Array.isArray(parsed)) return { data: { files: parsed.map((results) => ({ attachmentBase: null, results })) }, error: null }
    return { data: { files: parsed.files ?? [] }, error: null }
  } catch (e) {
    return { data: { files: [] }, error: `The results inlined in this page could not be read: ${(e as Error).message}` }
  }
}

export function attachmentsOf(attachments: ResultsAttachment[] | null | undefined, base: string | null): StepAttachment[] {
  return (attachments ?? []).map((a) => ({
    name: a.name ?? '',
    mediaType: a.mediaType ?? 'application/octet-stream',
    size: a.size ?? 0,
    href: base !== null && a.path ? base + a.path.split('/').map(encodeURIComponent).join('/') : null,
    omitted: a.omitted ?? null,
  }))
}

/** A scenario as the page renders it: what `SpecificationView` reads, and the rest of the file's. */
export interface ResultsScenarioView {
  /** `{Feature}/{Scenario}`, or the title for a scenario with no feature. */
  id: string
  feature: string
  title: string
  status: ScenarioStatus
  /** The file's own word, verbatim: passed, failed, "acceptance: failing", "acceptance: passing". */
  verdict: string
  durationMs: number | null
  counts: { rights: number; wrongs: number; errors: number }
  steps: StepState[]
  declaredSteps: DeclaredStepInfo[]
  plannedSteps: PlannedStepInfo[]
  errorMessage: string | null
  scenarioFailureCells: StepCell[]
  logs: string[]
  suppressedLogs: number
  reports: { title: string; columns: string[]; cells: StepCell[]; suppressedRows: number }[]
  attachments: StepAttachment[]
  suppressedAttachments: number
}

export function statusOfVerdict(verdict: string | undefined, succeeded: boolean | undefined): ScenarioStatus {
  switch (verdict) {
    case 'passed':
    case 'acceptance: passing':
      return 'passed'
    case 'acceptance: failing':
      return 'acceptance-failing'
    case 'failed':
      return 'failed'
    default:
      return succeeded ? 'passed' : 'failed'
  }
}

/** The results file's ResultStatus words; `ok` (prose, an unjudged value) and `success` pass. */
function passed(status: string | undefined): boolean {
  return status === 'success' || status === 'ok'
}

function cellOf(cell: ResultsCell, rowIndex: number): StepCell {
  return {
    name: cell.column ?? '',
    status: cell.status ?? 'ok',
    expected: cell.expected ?? null,
    actual: cell.actual ?? null,
    note: cell.note ?? null,
    rowIndex,
    value: cell.value ?? null,
    comparison: null,
  }
}

/**
 * A grid as the board's cells: one cell per column per row, and an absent row as Bobcat's own
 * convention on the wire — ONE `missing-row` / `extra-row` marker cell carrying the verdict and the
 * row's description, beside the row's values (stoat#63) — so `cellsOf` folds it the same way.
 */
export function gridCells(grid: ResultsGrid): StepCell[] {
  const cells: StepCell[] = []
  for (const row of grid.rows ?? []) {
    const index = row.row ?? 0
    const marker = row.type === 'Missing' ? 'missing-row' : row.type === 'Extra' ? 'extra-row' : null
    if (marker) {
      cells.push({
        name: marker,
        status: row.type === 'Missing' ? 'missing' : 'invalid',
        expected: null,
        actual: null,
        note: row.description ?? null,
        rowIndex: index,
        value: row.description ?? null,
        comparison: null,
      })
    }
    for (const cell of row.cells ?? []) cells.push(cellOf(cell, index))
  }
  return cells
}

function withSuppressed(lines: string[] | null | undefined, suppressed: number | null | undefined): string[] {
  const kept = [...(lines ?? [])]
  if (suppressed && suppressed > 0) kept.push(`…and ${suppressed} more`)
  return kept
}

function stepOf(step: ResultsStep, base: string | null): StepState {
  // A step with a grid renders the grid: its flat `cells` are the same comparisons without rows.
  const grid = step.setVerification
  const cells = grid ? gridCells(grid) : (step.cells ?? []).map((c) => cellOf(c, -1))

  return {
    stepId: step.stepId ?? '',
    // The board reads `kind` as the label it prints, and an empty keyword prints none: a step that
    // spells no keyword must not be given one.
    kind: step.keyword ?? step.kind ?? '',
    text: step.text ?? '',
    status: passed(step.status) ? 'passed' : 'failed',
    durationMs: step.durationMs ?? null,
    errorMessage: step.error ?? null,
    stepNumber: null,
    scenarioElapsedMs: step.startedAtMs ?? null,
    progress: null,
    declaredStepNumber: null,
    plannedStepNumber: null,
    // The file carries no value spans, so a sentence renders as prose throughout.
    values: [],
    cells,
    columns: grid?.columns ?? [],
    logs: withSuppressed(step.logs, step.suppressedLogs),
    diagnostics: step.diagnostics ?? {},
    // The file names an exception's type only when it was a real throw worth a stack; it carries no
    // stack, so the panel shows the type and message alone.
    exception: step.exceptionType
      ? { type: step.exceptionType, message: step.error ?? null, frames: [], hiddenFrames: 0, stackTrace: null }
      : null,
    attachments: attachmentsOf(step.attachments, base),
  }
}

/**
 * The file's flat step list as the board's three lists. A marker comment (`narrative`) is a
 * declared sentence and the steps nested under it (`depth` ≥ 1) ran inside it. A step the scenario
 * never reached (`notRun`) is a planned step nothing reached, so every other step is given a plan
 * position too — `linesOf` then puts the unreached ones back where they would have run.
 */
export function scenarioOf(scenario: ResultsScenario, base: string | null): ResultsScenarioView {
  const declaredSteps: DeclaredStepInfo[] = []
  const plannedSteps: PlannedStepInfo[] = []
  const steps: StepState[] = []
  let declared: number | null = null

  for (const step of scenario.steps ?? []) {
    if (step.narrative) {
      declaredSteps.push({ keyword: step.keyword ?? step.kind ?? '', text: step.text ?? '' })
      declared = declaredSteps.length
      continue
    }

    if (!step.depth) declared = null

    plannedSteps.push({
      keyword: step.keyword ?? step.kind ?? '',
      template: step.text ?? '',
      grammar: '',
      declaredStepNumber: declared,
    })

    if (step.notRun) continue

    const state = stepOf(step, base)
    state.declaredStepNumber = declared
    state.plannedStepNumber = plannedSteps.length
    steps.push(state)
  }

  const feature = scenario.feature ?? ''
  const title = scenario.title ?? ''

  return {
    id: scenario.id ?? (feature ? `${feature}/${title}` : title),
    feature,
    title,
    status: statusOfVerdict(scenario.verdict, scenario.succeeded),
    verdict: scenario.verdict ?? (scenario.succeeded ? 'passed' : 'failed'),
    durationMs: scenario.durationMs ?? null,
    counts: {
      rights: scenario.counts?.rights ?? 0,
      wrongs: scenario.counts?.wrongs ?? 0,
      errors: scenario.counts?.errors ?? 0,
    },
    steps,
    declaredSteps,
    plannedSteps,
    errorMessage: scenario.scenarioFailure ?? null,
    scenarioFailureCells: (scenario.scenarioFailureCells ?? []).map((c) => cellOf(c, -1)),
    logs: scenario.logs ?? [],
    suppressedLogs: scenario.suppressedLogs ?? 0,
    reports: (scenario.reports ?? []).map((report) => ({
      title: report.title ?? '',
      columns: report.grid?.columns ?? [],
      cells: report.grid ? gridCells(report.grid) : [],
      suppressedRows: report.suppressedRows ?? 0,
    })),
    attachments: attachmentsOf(scenario.attachments, base),
    suppressedAttachments: scenario.suppressedAttachments ?? 0,
  }
}

export interface FeatureGroup {
  feature: string
  scenarios: ResultsScenarioView[]
  failing: number
}

/** True for a scenario that fails the run — the one a reader opened the page to find. */
export function failsTheRun(scenario: ResultsScenarioView): boolean {
  return scenario.status === 'failed'
}

/**
 * Every scenario of every file, grouped by feature in the console's order (feature, then title),
 * so a supervised run's several files read as one run.
 */
export function featuresOf(data: ResultsPageData): FeatureGroup[] {
  const all = data.files.flatMap((file) =>
    (file.results.scenarios ?? []).map((scenario) => scenarioOf(scenario, file.attachmentBase)),
  )
  const byFeature = new Map<string, ResultsScenarioView[]>()
  for (const scenario of all) {
    byFeature.set(scenario.feature, [...(byFeature.get(scenario.feature) ?? []), scenario])
  }
  return [...byFeature.keys()]
    .sort(ordinal)
    .map((feature) => {
      const scenarios = byFeature.get(feature)!.sort((a, b) => ordinal(a.title, b.title))
      return { feature, scenarios, failing: scenarios.filter(failsTheRun).length }
    })
}

function ordinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0
}

export interface PageSummary {
  scenarios: number
  passed: number
  failed: number
  acceptanceFailing: number
  succeeded: boolean
  rights: number
  wrongs: number
  errors: number
}

/** The files' summaries added up: a supervised run writes one file per worker under one run id. */
export function summaryOf(data: ResultsPageData): PageSummary {
  const total: PageSummary = {
    scenarios: 0,
    passed: 0,
    failed: 0,
    acceptanceFailing: 0,
    succeeded: true,
    rights: 0,
    wrongs: 0,
    errors: 0,
  }
  for (const { results } of data.files) {
    const s = results.summary
    if (!s) continue
    total.scenarios += s.scenarios ?? 0
    total.passed += s.passed ?? 0
    total.failed += s.failed ?? 0
    total.acceptanceFailing += s.acceptanceFailing ?? 0
    total.succeeded &&= s.succeeded ?? false
    total.rights += s.rights ?? 0
    total.wrongs += s.wrongs ?? 0
    total.errors += s.errors ?? 0
  }
  return total
}
