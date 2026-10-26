import type { PlannedStepInfo, StepCell, StepValueSpan } from '@/messages/monitor-events'
import type { ScenarioState, StepState } from '@/stores/runs-store'
import { narrativeFor, type NarrativeStatus } from './narrative'

/**
 * One rendered specification (stoat#61), whichever lane authored it.
 *
 * A `.feature` scenario and a projected xUnit/TUnit test are the same artifact seen from two
 * authoring styles, and Bobcat renders both through one model (`SpecRender`) for exactly that
 * reason. This is Stoat's one model, and every view that shows a specification — a run, the
 * standalone spec page, the Event Model's slice drill-down — renders through it. Two viewers is
 * how one of them quietly stops being improved.
 *
 * Built on the #304 narrative rather than beside it: a marker comment's declared sentence is
 * still the backbone, and still reports only its children. What this adds is everything a line
 * can say about itself — cells, input values, logs — and the lines a run never reached.
 */

/**
 * `errored` is a step that ended in an exception, kept apart from `failed` (an assertion that
 * disagreed) because the two render differently: an exception's stack goes to the bottom of the
 * specification, a disagreement's message stays on the line (stoat#60).
 *
 * `not-run` and `pending` are PLANNED steps no recorded step reached (stoat#59). Which one depends
 * on the scenario, not the step: once it has a verdict the rest of the plan was not run; while it
 * is still going, the rest of the plan simply has not happened yet.
 */
export type LineStatus = 'running' | 'passed' | 'failed' | 'errored' | 'not-run' | 'pending'

export type SpecLine =
  | { kind: 'step'; key: string; status: LineStatus; step: StepState }
  | {
      kind: 'planned'
      key: string
      status: 'not-run' | 'pending'
      /** 1-based position in the plan. */
      number: number
      planned: PlannedStepInfo
    }

export interface SpecRow {
  /** 1-based position in the declared narrative. */
  number: number
  keyword: string
  text: string
  status: NarrativeStatus
  observedMs: number | null
  lines: SpecLine[]
}

/** An exception, rendered once, at the bottom of the specification (stoat#60). */
export interface ExceptionPanel {
  key: string
  /** The sentence of the step that threw — what the reader scans the panel list by. */
  title: string
  type: string | null
  /** The type without its namespace — a viewer's decision about its own width, not the wire's. */
  shortType: string | null
  message: string | null
  /** Bobcat's filtered frames, with file paths shortened to the file name. */
  frames: string[]
  hiddenFrames: number
  stackTrace: string | null
}

export interface Specification {
  /** The declared narrative, each sentence with its lines. Empty when nothing was declared. */
  rows: SpecRow[]
  /** Lines under no declared sentence — for every lane but marker comments, all of them. */
  outside: SpecLine[]
  exceptions: ExceptionPanel[]
  /**
   * Why the scenario failed when no line accounts for it — an assertion library that threw
   * between steps, say. Null whenever a line already explains the failure: repeating it would turn
   * the one sentence a reader most needs into boilerplate they learn to skip.
   */
  scenarioFailure: string | null
}

/**
 * What a specification is rendered from: a live scenario, or one read from a results file. The
 * reports (bobcat#408) are optional because a source may predate them; the viewer renders what it
 * is given.
 */
export type SpecSource = Pick<
  ScenarioState,
  'status' | 'steps' | 'declaredSteps' | 'plannedSteps' | 'errorMessage'
> &
  Partial<Pick<ScenarioState, 'reports'>>

function statusOfStep(step: StepState): LineStatus {
  if (step.status === 'running') return 'running'
  if (step.exception) return 'errored'
  return step.status
}

/**
 * The lines of the current attempt in the order a reader wants them: the plan's order, with the
 * recorded steps where they happened and the planned steps nobody reached where they would have
 * been.
 *
 * Reached-ness is a set difference over `plannedStepNumber`, a call-site ordinal the generator
 * assigned, so it survives a helper called twice, in a loop, or behind an `if` — and nothing is
 * matched by text. A recorded step with no plan position keeps its recorded place. Without a plan
 * there are no unreached lines at all: nothing says a step was ever going to be taken.
 */
export function linesOf(scenario: SpecSource): SpecLine[] {
  const planned = scenario.plannedSteps
  const unreachedStatus: 'not-run' | 'pending' = scenario.status === 'running' ? 'pending' : 'not-run'
  const stepLine = (step: StepState, index: number): SpecLine => ({
    kind: 'step',
    key: `step-${index}`,
    status: statusOfStep(step),
    step,
  })

  if (planned.length === 0) return scenario.steps.map(stepLine)

  const reached = new Set<number>()
  for (const step of scenario.steps) {
    if (step.plannedStepNumber != null) reached.add(step.plannedStepNumber)
  }

  const lines: SpecLine[] = []
  let next = 1
  const unreachedBefore = (limit: number) => {
    for (; next < limit; next++) {
      if (reached.has(next)) continue
      lines.push({
        kind: 'planned',
        key: `planned-${next}`,
        status: unreachedStatus,
        number: next,
        planned: planned[next - 1]!,
      })
    }
  }

  scenario.steps.forEach((step, index) => {
    const position = step.plannedStepNumber
    // A position past the end of the plan belongs to nothing — the same refusal narrative.ts makes
    // for a declared number past the end of the narrative.
    if (position != null && position >= 1 && position <= planned.length) {
      unreachedBefore(position)
      next = Math.max(next, position + 1)
    }
    lines.push(stepLine(step, index))
  })
  unreachedBefore(planned.length + 1)

  return lines
}

function declaredNumberOf(line: SpecLine): number | null {
  return line.kind === 'step' ? line.step.declaredStepNumber : line.planned.declaredStepNumber
}

/** The type without its namespace: `System.DivideByZeroException` → `DivideByZeroException`. */
export function shortTypeName(type: string | null): string | null {
  if (!type) return null
  // Generic arguments carry dots of their own; only the part before them names the type.
  const bracket = type.search(/[`<[]/)
  const head = bracket > 0 ? type.slice(0, bracket) : type
  const dot = head.lastIndexOf('.')
  return dot >= 0 ? type.slice(dot + 1) : type
}

/**
 * A frame with its file path cut to the file name: `at X() in /src/app/Calc.cs:line 107` →
 * `at X() in Calc.cs:107`. Shortening is presentation and is deliberately not on the wire.
 */
export function shortenFrame(frame: string): string {
  return frame.replace(/ in (?:.*[/\\])?([^/\\]+?)(?::line |:)(\d+)\s*$/, ' in $1:$2')
}

function exceptionsOf(lines: SpecLine[]): ExceptionPanel[] {
  const panels: ExceptionPanel[] = []
  for (const line of lines) {
    if (line.kind !== 'step' || !line.step.exception) continue
    const { exception, text } = line.step
    panels.push({
      key: line.key,
      title: text,
      type: exception.type,
      shortType: shortTypeName(exception.type),
      message: exception.message,
      frames: exception.frames.map(shortenFrame),
      hiddenFrames: exception.hiddenFrames,
      stackTrace: exception.stackTrace,
    })
  }
  return panels
}

export function specificationFor(scenario: SpecSource): Specification {
  const lines = linesOf(scenario)
  const narrative = narrativeFor(scenario)

  const rows: SpecRow[] = narrative.rows.map((row) => ({
    number: row.number,
    keyword: row.keyword,
    text: row.text,
    status: row.status,
    observedMs: row.observedMs,
    lines: lines.filter((line) => declaredNumberOf(line) === row.number),
  }))

  const outside = lines.filter((line) => {
    const declared = declaredNumberOf(line)
    return declared === null || declared < 1 || declared > rows.length
  })

  const explained = lines.some(
    (line) => line.kind === 'step' && (line.status === 'failed' || line.status === 'errored'),
  )
  const failedScenario = scenario.status === 'failed' || scenario.status === 'aborted'

  return {
    rows,
    outside,
    exceptions: exceptionsOf(lines),
    scenarioFailure: failedScenario && !explained ? scenario.errorMessage : null,
  }
}

export interface TextSegment {
  text: string
  /** True when this run of characters came from an input value (stoat#59). */
  value: boolean
}

/**
 * A sentence cut into prose and input values, from the spans the publisher sent. Spans are
 * trusted for WHERE, never recomputed from the text; a span out of range or overlapping an earlier
 * one is dropped rather than allowed to garble the sentence.
 */
export function segmentsOf(text: string, values: StepValueSpan[]): TextSegment[] {
  if (text.length === 0) return []
  const spans = values
    .filter((v) => v.length > 0 && v.start >= 0 && v.start < text.length)
    .sort((a, b) => a.start - b.start)

  const segments: TextSegment[] = []
  let at = 0
  for (const span of spans) {
    if (span.start < at) continue
    if (span.start > at) segments.push({ text: text.slice(at, span.start), value: false })
    const end = Math.min(text.length, span.start + span.length)
    segments.push({ text: text.slice(span.start, end), value: true })
    at = end
  }
  if (at < text.length) segments.push({ text: text.slice(at), value: false })
  return segments
}

/** How a cell is coloured: the framework's own word, folded to the four a reader can see. */
export type CellVerdict = 'passed' | 'failed' | 'errored' | 'other'

/**
 * `missing` and `invalid` are failures — they are two of ResultStatus's six words, and a cell
 * carrying either did not match (stoat#63). Folding them into `other` left them uncoloured, which
 * on a set verification read as "not a failure".
 */
export function verdictOf(cell: StepCell): CellVerdict {
  switch (cell.status) {
    case 'success':
    case 'ok':
      return 'passed'
    case 'failed':
    case 'missing':
    case 'invalid':
      return 'failed'
    case 'error':
      return 'errored'
    default:
      return 'other'
  }
}

/**
 * A row a set verification reported as absent from one side (stoat#63): `missing` — the
 * specification expected it and it was not found — or `extra` — it was found and the
 * specification does not describe it.
 */
export type RowMarker = 'missing' | 'extra'

/**
 * Bobcat's convention for an absent row (bobcat 602d7f4): ONE marker cell named `missing-row` or
 * `extra-row` carrying the verdict, beside one `ok` cell per column carrying the row's values —
 * expected ones for a missing row, actual ones for an extra row. The siblings are `ok` on purpose,
 * so the row counts as exactly one failure; they say WHICH row, and the marker says what is wrong
 * with it. So a marker is a property of the row and never a column of its own.
 */
export const ROW_MARKERS: Readonly<Record<string, RowMarker>> = {
  'missing-row': 'missing',
  'extra-row': 'extra',
}

export function rowMarkerOf(cell: StepCell): RowMarker | null {
  return cell.rowIndex >= 0 ? (ROW_MARKERS[cell.name] ?? null) : null
}

export interface CellTableRow {
  index: number
  cells: (StepCell | null)[]
  /** Set when the row is absent from one side; its cells then carry the row's values, not verdicts. */
  marker: RowMarker | null
}

export interface CellTable {
  columns: string[]
  rows: CellTableRow[]
}

/**
 * A step's cells split the way they render (stoat#58): the ones that belong to the sentence
 * (`rowIndex < 0`), and the ones that form a table, reassembled into a grid from the step's
 * `columns` and each cell's row. A table cell whose name the columns do not list still gets a
 * column — dropping a comparison because a publisher forgot to announce it would hide exactly the
 * cell someone needs. A row marker (`missing-row` / `extra-row`) is the exception: it becomes the
 * row's `marker`, never a column (stoat#63).
 */
function lastNamed(cells: StepCell[], name: string): StepCell | null {
  for (let i = cells.length - 1; i >= 0; i--) {
    const cell = cells[i]
    if (cell?.name === name) return cell
  }
  return null
}

export function cellsOf(cells: StepCell[], columns: string[]): { sentence: StepCell[]; table: CellTable | null } {
  const sentence = cells.filter((c) => c.rowIndex < 0)
  const tabled = cells.filter((c) => c.rowIndex >= 0)
  if (tabled.length === 0) return { sentence, table: null }

  const names = [...columns]
  for (const cell of tabled) {
    if (rowMarkerOf(cell)) continue
    if (!names.includes(cell.name)) names.push(cell.name)
  }

  const byRow = new Map<number, StepCell[]>()
  for (const cell of tabled) {
    const row = byRow.get(cell.rowIndex) ?? []
    row.push(cell)
    byRow.set(cell.rowIndex, row)
  }

  const rows = [...byRow.keys()]
    .sort((a, b) => a - b)
    .map((index) => {
      const row = byRow.get(index)!
      const marker = row.map(rowMarkerOf).find((m) => m !== null) ?? null
      const values = row.filter((c) => rowMarkerOf(c) === null)
      // The LAST cell for a column wins: a decision table sends the literal's `ok` cell first and
      // the comparison for the same column after it, which supersedes it — the README's own words.
      // Taking the first showed the empty literal over a red "expected '5', got '4'" (found live on
      // a slow table, stoat#62).
      return { index, marker, cells: names.map((name) => lastNamed(values, name)) }
    })

  return { sentence, table: { columns: names, rows } }
}
