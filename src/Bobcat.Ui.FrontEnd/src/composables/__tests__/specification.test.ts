import { describe, expect, it } from 'vitest'
import {
  cellsOf,
  linesOf,
  verdictOf,
  segmentsOf,
  shortenFrame,
  shortTypeName,
  specificationFor,
  type SpecLine,
} from '../specification'
import type { PlannedStepInfo } from '@/messages/monitor-events'
import type { ScenarioState, StepState } from '@/stores/runs-store'

/**
 * stoat#58–#61 — the one rendering model every specification view goes through. The rules under
 * test are the ones a viewer gets wrong by guessing: which planned steps were reached (a set
 * difference, never a text match), where the input values are (carried, never searched for), and
 * which failures are exceptions (the wire says; the message is never sniffed).
 */
function step(overrides: Partial<StepState> & { stepId: string }): StepState {
  return {
    kind: 'Given',
    text: 'a step',
    status: 'passed',
    durationMs: 1,
    errorMessage: null,
    stepNumber: null,
    scenarioElapsedMs: null,
    progress: null,
    declaredStepNumber: null,
    plannedStepNumber: null,
    values: [],
    cells: [],
    columns: [],
    logs: [],
    diagnostics: {},
    exception: null,
    ...overrides,
  }
}

type Source = Pick<ScenarioState, 'status' | 'steps' | 'declaredSteps' | 'plannedSteps' | 'errorMessage'>

function source(overrides: Partial<Source> = {}): Source {
  return { status: 'failed', steps: [], declaredSteps: [], plannedSteps: [], errorMessage: null, ...overrides }
}

const plan = (...templates: string[]): PlannedStepInfo[] =>
  templates.map((template) => ({ keyword: 'Then', template, grammar: `G.${template}`, declaredStepNumber: null }))

function describeLines(lines: SpecLine[]): string[] {
  return lines.map((line) =>
    line.kind === 'step' ? `${line.status}:${line.step.stepId}` : `${line.status}:#${line.number} ${line.planned.template}`,
  )
}

describe('linesOf — planned steps a run never reached (stoat#59)', () => {
  it('renders the steps after an exception as not run, with their templates unresolved', () => {
    const lines = linesOf(
      source({
        plannedSteps: plan('Start with {value}', 'Divide by zero', 'The value should be {value}'),
        steps: [
          step({ stepId: 'start', plannedStepNumber: 1 }),
          step({
            stepId: 'divide',
            plannedStepNumber: 2,
            status: 'failed',
            exception: { type: 'System.DivideByZeroException', message: 'x', frames: [], hiddenFrames: 0, stackTrace: null },
          }),
        ],
      }),
    )

    expect(describeLines(lines)).toEqual([
      'passed:start',
      'errored:divide',
      'not-run:#3 The value should be {value}',
    ])
  })

  it('calls the rest of the plan pending, not "not run", while the scenario is still going', () => {
    const lines = linesOf(
      source({
        status: 'running',
        plannedSteps: plan('a', 'b'),
        steps: [step({ stepId: 'a', plannedStepNumber: 1, status: 'running' })],
      }),
    )

    expect(describeLines(lines)).toEqual(['running:a', 'pending:#2 b'])
  })

  it('puts a skipped branch in the middle where it would have been', () => {
    const lines = linesOf(
      source({
        plannedSteps: plan('first', 'only if', 'last'),
        steps: [step({ stepId: 'first', plannedStepNumber: 1 }), step({ stepId: 'last', plannedStepNumber: 3 })],
      }),
    )

    expect(describeLines(lines)).toEqual(['passed:first', 'not-run:#2 only if', 'passed:last'])
  })

  it('matches by call-site ordinal, so a helper in a loop renders every run and invents nothing', () => {
    // Same text on every pass, and the plan has each call site once: matching by text would have
    // collapsed the loop or flagged its second pass as unplanned.
    const lines = linesOf(
      source({
        plannedSteps: plan('add {n}', 'check {n}', 'never'),
        steps: [
          step({ stepId: 'add', plannedStepNumber: 1 }),
          step({ stepId: 'check', plannedStepNumber: 2 }),
          step({ stepId: 'add', plannedStepNumber: 1 }),
          step({ stepId: 'check', plannedStepNumber: 2 }),
        ],
      }),
    )

    expect(describeLines(lines)).toEqual([
      'passed:add',
      'passed:check',
      'passed:add',
      'passed:check',
      'not-run:#3 never',
    ])
  })

  it('has no unreached lines without a plan — nothing said a step was ever going to be taken', () => {
    const lines = linesOf(source({ steps: [step({ stepId: 'a' }), step({ stepId: 'b', status: 'failed' })] }))
    expect(describeLines(lines)).toEqual(['passed:a', 'failed:b'])
  })

  it('keeps a recorded step with no plan position in its recorded place', () => {
    const lines = linesOf(
      source({
        plannedSteps: plan('one', 'two'),
        steps: [step({ stepId: 'one', plannedStepNumber: 1 }), step({ stepId: 'loose' })],
      }),
    )
    expect(describeLines(lines)).toEqual(['passed:one', 'passed:loose', 'not-run:#2 two'])
  })
})

describe('specificationFor', () => {
  it('files planned lines under the marker comment they sit beneath', () => {
    const spec = specificationFor(
      source({
        declaredSteps: [
          { keyword: 'Given', text: 'a calculator' },
          { keyword: 'Then', text: 'the sums add up' },
        ],
        plannedSteps: [
          { keyword: 'Given', template: 'Start with {v}', grammar: 'G.Start', declaredStepNumber: 1 },
          { keyword: 'Then', template: 'The sum is {s}', grammar: 'G.Sum', declaredStepNumber: 2 },
        ],
        steps: [step({ stepId: 'start', plannedStepNumber: 1, declaredStepNumber: 1, status: 'failed' })],
      }),
    )

    expect(spec.rows.map((r) => describeLines(r.lines))).toEqual([['failed:start'], ['not-run:#2 The sum is {s}']])
    expect(spec.outside).toEqual([])
  })

  it('collects each exception once, for the bottom of the specification, titled by its sentence', () => {
    const spec = specificationFor(
      source({
        steps: [
          step({ stepId: 'ok' }),
          step({
            stepId: 'boom',
            text: 'Divide by zero using integer maths',
            status: 'failed',
            errorMessage: 'Attempted to divide by zero.',
            exception: {
              type: 'System.DivideByZeroException',
              message: 'Attempted to divide by zero.',
              frames: ['at Calc.Divide() in /Users/me/src/app/CalculatorGrammar.cs:line 107'],
              hiddenFrames: 3,
              stackTrace: 'raw',
            },
          }),
        ],
      }),
    )

    expect(spec.exceptions).toHaveLength(1)
    const panel = spec.exceptions[0]!
    expect(panel.title).toBe('Divide by zero using integer maths')
    expect(panel.shortType).toBe('DivideByZeroException')
    expect(panel.frames).toEqual(['at Calc.Divide() in CalculatorGrammar.cs:107'])
    expect(panel.hiddenFrames).toBe(3)
  })

  it('reports no exception for an assertion that merely disagreed — nothing was thrown', () => {
    const spec = specificationFor(
      source({ steps: [step({ stepId: 'sum', status: 'failed', errorMessage: "Sum: expected '6', got '8'" })] }),
    )
    expect(spec.exceptions).toEqual([])
    expect(spec.scenarioFailure).toBeNull()
  })

  it('says why a scenario failed when no line accounts for it, and only then', () => {
    const unexplained = specificationFor(
      source({ steps: [step({ stepId: 'a' })], errorMessage: 'Shouldly: expected 1 but was 2' }),
    )
    expect(unexplained.scenarioFailure).toBe('Shouldly: expected 1 but was 2')

    const explained = specificationFor(
      source({ steps: [step({ stepId: 'a', status: 'failed' })], errorMessage: 'same failure' }),
    )
    expect(explained.scenarioFailure).toBeNull()

    expect(specificationFor(source({ status: 'passed', errorMessage: 'stale' })).scenarioFailure).toBeNull()
  })
})

describe('segmentsOf — input values (stoat#59)', () => {
  it('marks the value where the span says, not where the text first matches', () => {
    // The case that makes the spans travel: "3 of them cost 3", where the value is the SECOND 3.
    const segments = segmentsOf('3 of them cost 3', [{ start: 15, length: 1 }])
    expect(segments).toEqual([
      { text: '3 of them cost ', value: false },
      { text: '3', value: true },
    ])
  })

  it('handles several values and prose on both sides', () => {
    expect(segmentsOf('For X=4 and Y=5, done', [{ start: 14, length: 1 }, { start: 6, length: 1 }])).toEqual([
      { text: 'For X=', value: false },
      { text: '4', value: true },
      { text: ' and Y=', value: false },
      { text: '5', value: true },
      { text: ', done', value: false },
    ])
  })

  it('drops a span that is out of range or overlaps an earlier one rather than garbling the sentence', () => {
    expect(
      segmentsOf('abc', [
        { start: 0, length: 2 },
        { start: 1, length: 1 },
        { start: 9, length: 1 },
        { start: 2, length: 0 },
      ]),
    ).toEqual([
      { text: 'ab', value: true },
      { text: 'c', value: false },
    ])
  })
})

describe('cellsOf — cells as a sentence and as a grid (stoat#58)', () => {
  it('keeps sentence cells apart from table cells', () => {
    const { sentence, table } = cellsOf(
      [
        { name: 'Sum', status: 'failed', expected: '6', actual: '8', note: null, rowIndex: -1, value: null, comparison: null },
        { name: 'Product', status: 'success', expected: '16', actual: '16', note: null, rowIndex: -1, value: null, comparison: null },
      ],
      [],
    )
    expect(sentence.map((c) => c.name)).toEqual(['Sum', 'Product'])
    expect(table).toBeNull()
  })

  it('reassembles a grid from the columns and each cell’s row, leaving a gap where no cell came', () => {
    const { table } = cellsOf(
      [
        { name: 'Name', status: 'success', expected: 'Bill', actual: 'Bill', note: null, rowIndex: 0, value: null, comparison: null },
        { name: 'Age', status: 'failed', expected: '40', actual: '41', note: null, rowIndex: 0, value: null, comparison: null },
        { name: 'Name', status: 'success', expected: 'Ann', actual: 'Ann', note: null, rowIndex: 1, value: null, comparison: null },
      ],
      ['Name', 'Age'],
    )

    expect(table!.columns).toEqual(['Name', 'Age'])
    expect(table!.rows.map((r) => r.cells.map((c) => c?.actual ?? null))).toEqual([
      ['Bill', '41'],
      ['Ann', null],
    ])
  })

  // stoat#62, found live: a decision table's literal cell comes first and the comparison for the
  // same column after it. Showing the first hid every verdict behind the literal.
  it('lets the comparison for a column supersede the literal cell sent before it', () => {
    const { table } = cellsOf(
      [
        { name: 'x', status: 'ok', expected: null, actual: '2', note: null, rowIndex: 0, value: null, comparison: null },
        { name: 'sum', status: 'ok', expected: null, actual: '5', note: null, rowIndex: 0, value: null, comparison: null },
        { name: 'sum', status: 'failed', expected: '5', actual: '4', note: null, rowIndex: 0, value: null, comparison: null },
      ],
      ['x', 'sum'],
    )
    expect(table!.rows[0].cells.map((c) => c?.status)).toEqual(['ok', 'failed'])
    expect(table!.rows[0].cells[1]!.actual).toBe('4')
  })

  it('gives an unannounced cell a column rather than dropping the comparison', () => {
    const { table } = cellsOf(
      [{ name: 'Surprise', status: 'failed', expected: 'a', actual: 'b', note: null, rowIndex: 0, value: null, comparison: null }],
      ['Name'],
    )
    expect(table!.columns).toEqual(['Name', 'Surprise'])
  })
})

describe('cellsOf — a set verification’s absent rows (stoat#63)', () => {
  // Bobcat's convention (bobcat 602d7f4): one marker cell carries the verdict, and `ok` siblings
  // carry the row's values so the row counts as exactly one failure.
  const cell = (name: string, status: string, rowIndex: number, values: { expected?: string; actual?: string } = {}) => ({
    name,
    status,
    expected: values.expected ?? null,
    actual: values.actual ?? null,
    note: null,
    rowIndex,
    value: null,
    comparison: null,
  })

  it('makes a marker the row’s verdict, never a column of its own', () => {
    const { table } = cellsOf(
      [
        cell('Name', 'success', 0, { expected: 'Bill', actual: 'Bill' }),
        cell('missing-row', 'missing', 1),
        cell('Name', 'ok', 1, { expected: 'Ann' }),
        cell('extra-row', 'invalid', 2),
        cell('Name', 'ok', 2, { actual: 'Zed' }),
      ],
      ['Name'],
    )

    expect(table!.columns).toEqual(['Name'])
    expect(table!.rows.map((r) => r.marker)).toEqual([null, 'missing', 'extra'])
    expect(table!.rows[1]!.cells[0]!.expected).toBe('Ann')
    expect(table!.rows[2]!.cells[0]!.actual).toBe('Zed')
  })

  it('reads a marker only inside a table — a sentence cell named missing-row is just a cell', () => {
    const { sentence, table } = cellsOf([cell('missing-row', 'missing', -1)], [])
    expect(sentence).toHaveLength(1)
    expect(table).toBeNull()
  })

  it('counts missing and invalid as failures, not as some other colour', () => {
    expect(verdictOf(cell('x', 'missing', -1))).toBe('failed')
    expect(verdictOf(cell('x', 'invalid', -1))).toBe('failed')
  })
})

describe('presentation of an exception', () => {
  it('shortens a type to its simple name, generics included', () => {
    expect(shortTypeName('System.DivideByZeroException')).toBe('DivideByZeroException')
    expect(shortTypeName('Foo.Bar`1[[System.Int32]]')).toBe('Bar`1[[System.Int32]]')
    expect(shortTypeName('NoNamespace')).toBe('NoNamespace')
    expect(shortTypeName(null)).toBeNull()
  })

  it('shortens a frame’s path to the file name, in both spellings of the line', () => {
    expect(shortenFrame('at A.B() in /repo/src/A.cs:line 12')).toBe('at A.B() in A.cs:12')
    expect(shortenFrame('at A.B() in C:\\repo\\A.cs:12')).toBe('at A.B() in A.cs:12')
    expect(shortenFrame('at A.B()')).toBe('at A.B()')
  })
})
