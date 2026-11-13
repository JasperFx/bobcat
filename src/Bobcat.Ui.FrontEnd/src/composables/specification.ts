import type { DeclaredStepInfo, PlannedStepInfo, StepCell, StepValueSpan } from '@/messages/monitor-events'
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
  | {
      kind: 'step'
      key: string
      status: LineStatus
      step: StepState
      /** The steps this one ran inside it (bobcat#810): a `[Given]` helper's own steps, nested under it. */
      children: SpecLine[]
    }
  | {
      kind: 'planned'
      key: string
      status: 'not-run' | 'pending'
      /** 1-based position in the plan. */
      number: number
      planned: PlannedStepInfo
      children: SpecLine[]
    }

/** One entry of the specification, top to bottom: a declared sentence, or a line under none. */
export type SpecItem = { kind: 'row'; row: SpecRow } | { kind: 'line'; line: SpecLine }

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
  /**
   * The rows and the outside lines together, in the order the test wrote them (bobcat#810): a helper
   * called before the first marker comment comes before it, as the console shows it. Without source
   * lines (an older publisher) the rows come first and the outside lines after them.
   */
  items: SpecItem[]
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
 * reports (bobcat#408) are optional because a source may predate them, and the scenario-level
 * stack trace (bobcat#825) because only a supervised foreign test has one; the viewer renders
 * what it is given.
 */
export type SpecSource = Pick<
  ScenarioState,
  'status' | 'steps' | 'declaredSteps' | 'plannedSteps' | 'errorMessage'
> &
  Partial<Pick<ScenarioState, 'reports' | 'stackTrace'>>

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
    children: [],
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
        children: [],
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

/**
 * The flat lines as a tree (bobcat#810): a step that ran inside another goes under it, the way the
 * console nests a helper's own steps. The parent is the nearest earlier step with that id — a
 * publisher that predates `stepId` being unique per occurrence repeats ids, and the one that was
 * open is the latest. A step whose parent is not there stays at the top.
 */
function nest(lines: SpecLine[]): SpecLine[] {
  const top: SpecLine[] = []
  const latest = new Map<string, SpecLine>()
  for (const line of lines) {
    const parentId = line.kind === 'step' ? line.step.parentStepId : null
    const parent = parentId ? latest.get(parentId) : undefined
    if (parent) parent.children.push(line)
    else top.push(line)
    if (line.kind === 'step') latest.set(line.step.stepId, line)
  }
  return top
}

/** Every line, the nested ones included, in order. */
function flatten(lines: SpecLine[]): SpecLine[] {
  return lines.flatMap((line) => [line, ...flatten(line.children)])
}

function lineOfLine(line: SpecLine, planned: PlannedStepInfo[]): number | null {
  const position = line.kind === 'planned' ? line.number : line.step.plannedStepNumber
  return position != null ? (planned[position - 1]?.line ?? null) : null
}

/**
 * The rows and the outside lines in source order (bobcat#810). A row sits at its comment's line and
 * an outside line at its call site's; a line with no call site (a step with no plan behind it) keeps
 * its place beside the line before it. When any row has no line — an older publisher — nothing can be
 * placed, and the rows come first as they always did.
 */
function itemsOf(rows: SpecRow[], outside: SpecLine[], declared: DeclaredStepInfo[], planned: PlannedStepInfo[]): SpecItem[] {
  const rowItems: SpecItem[] = rows.map((row) => ({ kind: 'row', row }))
  const lineItems: SpecItem[] = outside.map((line) => ({ kind: 'line', line }))
  const rowLines = declared.map((d) => d.line ?? null)
  if (rows.length === 0 || outside.length === 0 || rowLines.some((l) => l === null)) return [...rowItems, ...lineItems]

  // A line with no source line of its own borrows its neighbour's, so it stays beside it.
  const known = outside.map((line) => lineOfLine(line, planned))
  const placed = known.map((own, i) => {
    if (own !== null) return own
    for (let j = i - 1; j >= 0; j--) if (known[j] !== null) return known[j]!
    for (let j = i + 1; j < known.length; j++) if (known[j] !== null) return known[j]!
    return Number.POSITIVE_INFINITY
  })

  const items: SpecItem[] = []
  let r = 0
  outside.forEach((line, i) => {
    while (r < rows.length && rowLines[rows[r]!.number - 1]! < placed[i]!) items.push(rowItems[r++]!)
    items.push(lineItems[i]!)
  })
  while (r < rows.length) items.push(rowItems[r++]!)
  return items
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
  // A nested step belongs to the step it ran inside, wherever its own number points; only the top
  // lines are placed under a declared sentence or outside them.
  const top = nest(linesOf(scenario))
  const lines = flatten(top)
  const narrative = narrativeFor(scenario)

  const rows: SpecRow[] = narrative.rows.map((row) => ({
    number: row.number,
    keyword: row.keyword,
    text: row.text,
    status: row.status,
    observedMs: row.observedMs,
    lines: top.filter((line) => declaredNumberOf(line) === row.number),
  }))

  const outside = top.filter((line) => {
    const declared = declaredNumberOf(line)
    return declared === null || declared < 1 || declared > rows.length
  })

  const explained = lines.some(
    (line) => line.kind === 'step' && (line.status === 'failed' || line.status === 'errored'),
  )
  const failedScenario = scenario.status === 'failed' || scenario.status === 'aborted'
  const unexplained = failedScenario && !explained

  // A supervised foreign test (bobcat#825) has no steps to hang its exception on: its message is
  // the scenario failure and its capped stack gets the one panel a step's exception would.
  const exceptions = exceptionsOf(lines)
  if (unexplained && scenario.stackTrace) {
    exceptions.push({
      key: 'scenario',
      title: scenario.errorMessage ?? 'The test failed',
      type: null,
      shortType: null,
      message: null,
      frames: [],
      hiddenFrames: 0,
      stackTrace: scenario.stackTrace,
    })
  }

  return {
    rows,
    outside,
    items: itemsOf(rows, outside, scenario.declaredSteps, scenario.plannedSteps),
    exceptions,
    scenarioFailure: unexplained ? scenario.errorMessage : null,
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
