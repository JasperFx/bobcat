import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import SpecificationView from '../SpecificationView.vue'
import type { ScenarioState, StepState } from '@/stores/runs-store'

/**
 * The rendered specification (stoat#58–#62). Each case is a thing Storyteller showed and a red
 * row with no detail cannot: a named cell per comparison, a grid, the data in a sentence, the
 * steps a run never reached, and an exception kept out of the reader's way.
 */
function step(overrides: Partial<StepState> & { stepId: string }): StepState {
  return {
    kind: 'Then',
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

function scenario(overrides: Partial<ScenarioState> = {}): ScenarioState {
  return {
    uid: 'Calculator/divides',
    feature: 'Calculator',
    scenario: 'divides',
    status: 'failed',
    attempt: 1,
    attempts: 1,
    scheduledAttempt: null,
    outcome: 'Failed',
    durationMs: 10,
    errorMessage: null,
    retryReason: null,
    steps: [],
    totalSteps: null,
    declaredSteps: [],
    plannedSteps: [],
    touchedTypes: [],
    finishedAt: null,
    state: null,
    workerPublished: true,
    forwarded: false,
    displayName: null,
    ...overrides,
  }
}

const render = (s: ScenarioState) => mount(SpecificationView, { props: { scenario: s } })

describe('SpecificationView — cells (stoat#58)', () => {
  it('colours each cell on its own, so a wrong sum does not hide a right product', () => {
    const wrapper = render(
      scenario({
        steps: [
          step({
            stepId: 'check',
            status: 'failed',
            text: 'For X=4 and Y=4, the Sum should be 6 and the Product should be 8',
            errorMessage: "Sum: expected '6', got '8'",
            cells: [
              { name: 'Sum', status: 'failed', expected: '6', actual: '8', note: null, rowIndex: -1, value: null, comparison: null },
              { name: 'Product', status: 'success', expected: '16', actual: '16', note: null, rowIndex: -1, value: null, comparison: null },
            ],
          }),
        ],
      }),
    )

    const cells = wrapper.findAll('[data-testid="cell"]')
    expect(cells.map((c) => c.attributes('data-verdict'))).toEqual(['failed', 'passed'])
    expect(cells[0]!.text()).toContain("Sum: expected '6', got '8'")
    expect(cells[1]!.text()).toContain('Product: 16')
    // The cells already say what the flattened message said — it is not repeated on the line.
    expect(wrapper.find('[data-testid="line-message"]').exists()).toBe(false)
  })

  it('renders a set verification as a grid, each cell coloured', () => {
    const wrapper = render(
      scenario({
        steps: [
          step({
            stepId: 'set',
            status: 'failed',
            columns: ['Name', 'Age'],
            cells: [
              { name: 'Name', status: 'success', expected: 'Bill', actual: 'Bill', note: null, rowIndex: 0, value: null, comparison: null },
              { name: 'Age', status: 'failed', expected: '40', actual: '41', note: null, rowIndex: 0, value: null, comparison: null },
            ],
          }),
        ],
      }),
    )

    const grid = wrapper.find('[data-testid="cell-grid"]')
    expect(grid.findAll('th').map((th) => th.text())).toEqual(['Name', 'Age'])
    expect(grid.findAll('td').map((td) => td.attributes('data-verdict'))).toEqual(['passed', 'failed'])
    expect(grid.findAll('td')[1]!.text()).toBe("expected '40', got '41'")
  })

  // bobcat#396 (Bobcat 0.30.0): an input column's text rides in `value`, never in `actual`, and the
  // grid rendered every table literal's inputs empty until the mirror carried it.
  it('shows a decision table’s input columns from their value, beside the verdict column', () => {
    const wrapper = render(
      scenario({
        steps: [
          step({
            stepId: 'sums',
            status: 'failed',
            columns: ['x', 'y', 'sum'],
            cells: [
              { name: 'x', status: 'ok', expected: null, actual: null, note: null, rowIndex: 0, value: '2', comparison: null },
              { name: 'y', status: 'ok', expected: null, actual: null, note: null, rowIndex: 0, value: '2', comparison: null },
              { name: 'sum', status: 'ok', expected: null, actual: null, note: null, rowIndex: 0, value: '5', comparison: null },
              { name: 'sum', status: 'failed', expected: '5', actual: '4', note: null, rowIndex: 0, value: null, comparison: null },
            ],
          }),
        ],
      }),
    )

    const cells = wrapper.find('[data-testid="cell-grid"]').findAll('td').map((td) => td.text())
    expect(cells).toEqual(['2', '2', "expected '5', got '4'"])
  })

  it('renders a missing and an extra row as one failing row each, never as passed (stoat#63)', () => {
    const wrapper = render(
      scenario({
        steps: [
          step({
            stepId: 'set',
            status: 'failed',
            columns: ['Name', 'Age'],
            cells: [
              { name: 'Name', status: 'success', expected: 'Bill', actual: 'Bill', note: null, rowIndex: 0, value: null, comparison: null },
              { name: 'Age', status: 'success', expected: '40', actual: '40', note: null, rowIndex: 0, value: null, comparison: null },
              // Bobcat's absent rows: a marker, and `ok` siblings carrying the row's values.
              { name: 'missing-row', status: 'missing', expected: null, actual: null, note: null, rowIndex: 1, value: null, comparison: null },
              { name: 'Name', status: 'ok', expected: 'Ann', actual: null, note: null, rowIndex: 1, value: null, comparison: null },
              { name: 'Age', status: 'ok', expected: '33', actual: null, note: null, rowIndex: 1, value: null, comparison: null },
              { name: 'extra-row', status: 'invalid', expected: null, actual: null, note: null, rowIndex: 2, value: null, comparison: null },
              { name: 'Name', status: 'ok', expected: null, actual: 'Zed', note: null, rowIndex: 2, value: null, comparison: null },
            ],
          }),
        ],
      }),
    )

    const grid = wrapper.find('[data-testid="cell-grid"]')
    // No column for the markers.
    expect(grid.findAll('th').map((th) => th.text())).toEqual(['', 'Name', 'Age'])

    const rows = grid.findAll('tbody tr')
    expect(rows.map((r) => r.attributes('data-marker') ?? null)).toEqual([null, 'missing', 'extra'])

    // The missing row shows WHICH row — its expected values — and none of it reads as passed.
    const missing = rows[1]!
    expect(missing.find('[data-testid="row-marker"]').text()).toBe('missing')
    expect(missing.findAll('td').slice(1).map((td) => td.text())).toEqual(['Ann', '33'])
    expect(missing.findAll('td').some((td) => td.attributes('data-verdict') === 'passed')).toBe(false)

    // The extra row shows what was found, and a dash where the publisher sent no value.
    const extra = rows[2]!
    expect(extra.find('[data-testid="row-marker"]').text()).toBe('extra')
    expect(extra.findAll('td').slice(1).map((td) => td.text())).toEqual(['Zed', '—'])
    expect(extra.findAll('td').some((td) => td.attributes('data-verdict') === 'passed')).toBe(false)
  })

  it('renders logs and diagnostics under the step', () => {
    const wrapper = render(
      scenario({ steps: [step({ stepId: 's', logs: ['loaded 3 rows'], diagnostics: { tenant: 'blue' } })] }),
    )
    expect(wrapper.find('[data-testid="logs"]').text()).toContain('loaded 3 rows')
    expect(wrapper.find('[data-testid="diagnostics"]').text()).toContain('tenant')
    expect(wrapper.find('[data-testid="diagnostics"]').text()).toContain('blue')
  })

  it('keeps a disagreement’s message on the line when there are no cells to say it', () => {
    const wrapper = render(
      scenario({ steps: [step({ stepId: 's', status: 'failed', errorMessage: 'the email server is not reachable' })] }),
    )
    expect(wrapper.find('[data-testid="line-message"]').text()).toBe('the email server is not reachable')
  })
})

describe('SpecificationView — the plan, input values and keywords (stoat#59)', () => {
  it('greys the steps a run never reached and shows their templates unresolved', () => {
    const wrapper = render(
      scenario({
        plannedSteps: [
          { keyword: 'Given', template: 'Start with {value}', grammar: 'G.Start', declaredStepNumber: null },
          { keyword: 'Then', template: 'The value should be {value}', grammar: 'G.Value', declaredStepNumber: null },
        ],
        steps: [step({ stepId: 'start', kind: 'Given', text: 'Start with 3', plannedStepNumber: 1 })],
      }),
    )

    const lines = wrapper.findAll('[data-testid="spec-line"]')
    expect(lines.map((l) => l.attributes('data-status'))).toEqual(['passed', 'not-run'])
    expect(lines[1]!.text()).toContain('The value should be {value}')
    expect(lines[1]!.find('[data-testid="not-run"]').exists()).toBe(true)
  })

  it('italicises exactly the input values the publisher marked', () => {
    const wrapper = render(
      scenario({ steps: [step({ stepId: 's', text: '3 of them cost 3', values: [{ start: 15, length: 1 }] })] }),
    )
    const sentence = wrapper.find('[data-testid="line-sentence"]')
    expect(sentence.findAll('em').map((em) => em.text())).toEqual(['3'])
    expect(sentence.html()).toMatch(/^<span[^>]*>3 of them cost <em/)
  })

  it('renders an empty keyword as no label at all, and And as And', () => {
    const wrapper = render(
      scenario({
        steps: [
          step({ stepId: 'a', kind: '', text: 'The value should be 6' }),
          step({ stepId: 'b', kind: 'And', text: 'the log is empty' }),
        ],
      }),
    )
    const lines = wrapper.findAll('[data-testid="spec-line"]')
    expect(lines[0]!.find('[data-testid="line-keyword"]').exists()).toBe(false)
    expect(lines[0]!.text()).not.toContain('Then')
    expect(lines[1]!.find('[data-testid="line-keyword"]').text()).toBe('And')
  })
})

describe('SpecificationView — a line reads as a sentence', () => {
  it('has real spaces between glyph, keyword, sentence and duration, not just margins', () => {
    const wrapper = render(
      scenario({ steps: [step({ stepId: 's', kind: 'Given', text: 'Start with 3', durationMs: 1 })] }),
    )
    expect(wrapper.find('[data-testid="spec-line"]').text().replace(/\s+/g, ' ')).toBe('✓ Given Start with 3 1ms')
  })
})

describe('SpecificationView — the declared narrative', () => {
  it('keeps a space between a declared keyword and its sentence', () => {
    // Vue condenses a trailing space inside <strong>, which once rendered "Giventhe events are…".
    const wrapper = render(
      scenario({ declaredSteps: [{ keyword: 'Given', text: 'the events are published' }] }),
    )
    expect(wrapper.find('[data-declared="1"]').text()).toMatch(/^Given the events are published/)
  })
})

describe('SpecificationView — exceptions at the bottom (stoat#60)', () => {
  const thrown = scenario({
    steps: [
      step({ stepId: 'start', kind: 'Given', text: 'Start with 3' }),
      step({
        stepId: 'divide',
        kind: 'When',
        text: 'Divide by zero using integer maths',
        status: 'failed',
        errorMessage: 'Attempted to divide by zero.',
        exception: {
          type: 'System.DivideByZeroException',
          message: 'Attempted to divide by zero.',
          frames: ['at CalculatorGrammar.DivideByZero() in /src/CalculatorGrammar.cs:line 107'],
          hiddenFrames: 3,
          stackTrace: 'at Bobcat.Generated.__BobcatStep15()\nat CalculatorGrammar.DivideByZero()',
        },
      }),
    ],
  })

  it('marks the step with one line and puts the exception in a panel below the steps', () => {
    const wrapper = render(thrown)

    const line = wrapper.findAll('[data-testid="spec-line"]')[1]!
    expect(line.attributes('data-status')).toBe('errored')
    expect(line.find('[data-testid="exception-marker"]').text()).toBe('DivideByZeroException — see below')
    // The stack is NOT on the line.
    expect(line.text()).not.toContain('CalculatorGrammar.cs')

    const panel = wrapper.find('[data-testid="exception-panel"]')
    expect(panel.find('summary').text()).toBe('Divide by zero using integer maths')
    expect(panel.find('[data-testid="exception-frames"]').text()).toBe(
      'at CalculatorGrammar.DivideByZero() in CalculatorGrammar.cs:107',
    )
    expect(panel.find('[data-testid="hidden-frames"]').text()).toBe('(3 framework frames hidden)')
    expect(panel.text()).toContain('show every frame')
  })

  it('draws no panel for an assertion that disagreed', () => {
    const wrapper = render(scenario({ steps: [step({ stepId: 's', status: 'failed', errorMessage: 'nope' })] }))
    expect(wrapper.find('[data-testid="exception-panel"]').exists()).toBe(false)
  })
})

describe('SpecificationView — a slow step filling in (stoat#62)', () => {
  it('shows the cells made so far while the step is still running', () => {
    const wrapper = render(
      scenario({
        status: 'running',
        outcome: null,
        steps: [
          step({
            stepId: 'slow',
            status: 'running',
            durationMs: null,
            progress: {
              message: null,
              row: null,
              totalRows: null,
              elapsedMs: 3000,
              cells: [{ name: 'first', status: 'success', expected: '1', actual: '1', note: null, rowIndex: -1, value: null, comparison: null }],
            },
          }),
        ],
      }),
    )

    expect(wrapper.find('[data-testid="interim-cells"]').text()).toBe('so far')
    expect(wrapper.findAll('[data-testid="cell"]').map((c) => c.text())).toEqual(['✓ first: 1'])
  })
})
