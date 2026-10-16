import { describe, expect, it } from 'vitest'
import type { BobcatResults } from '@/messages/results-file'
import { cellsOf, specificationFor } from '@/composables/specification'
import fixture from './results.fixture.json'
import {
  featuresOf,
  readPageData,
  scenarioOf,
  summaryOf,
  type ResultsPageData,
} from '../fromResults'

/**
 * The results page (bobcat#541) reads the results FILE and renders through the board's own
 * specification model. The fixture is a real results file from Bobcat.Xunit.Samples, cut to six
 * scenarios, with a log, a report and attachments added in the file's own shape (the samples
 * write none).
 */
const results = fixture as BobcatResults

function scenario(id: string, base: string | null = '') {
  const found = results.scenarios!.find((s) => s.id === id)
  if (!found) throw new Error(`no ${id} in the fixture`)
  return scenarioOf(found, base)
}

describe('a scenario read from a results file', () => {
  it('turns marker comments into the declared narrative, with what ran inside each', () => {
    const spec = specificationFor(scenario('Calculator, narrated/the run throws before the next action'))

    expect(spec.rows.map((r) => r.text)).toEqual([
      'the value disagrees',
      'the calculator is doubled anyway',
      'this is never reached',
    ])
    expect(spec.rows[0]!.status).toBe('failed')
    expect(spec.rows[0]!.lines.map((l) => l.status)).toEqual(['failed'])
  })

  it('puts a step the run never reached back where it would have run, as not run', () => {
    const spec = specificationFor(scenario('Calculator, narrated/the run throws before the next action'))

    // The unreached step was nested under the third sentence.
    const unreached = spec.rows[2]!.lines
    expect(unreached).toHaveLength(1)
    expect(unreached[0]!.status).toBe('not-run')
    expect(unreached[0]!.kind === 'planned' && unreached[0]!.planned.template).toBe('calculator.Value should be 6')
  })

  it('keeps an exception apart from a disagreement, with the type and message the file names', () => {
    const spec = specificationFor(scenario('Calculator/an exception in an action stops the scenario'))

    expect(spec.outside.map((l) => l.status)).toEqual(['passed', 'errored', 'not-run'])
    expect(spec.exceptions).toHaveLength(1)
    expect(spec.exceptions[0]!.shortType).toBe('DivideByZeroException')
    expect(spec.exceptions[0]!.message).toBe('Attempted to divide by zero.')
  })

  it('renders a set verification as a grid, with a missing and an extra row each marked once', () => {
    const view = scenario('Sets/a set of names missing one and expecting one that is not there')
    const then = view.steps[1]!
    const { sentence, table } = cellsOf(then.cells, then.columns)

    expect(sentence).toEqual([])
    expect(table!.columns).toEqual(['Name'])
    expect(table!.rows.map((r) => r.marker)).toEqual([null, null, 'missing', 'extra'])
    expect(table!.rows[2]!.cells[0]!.expected).toBe('Leia')
    expect(table!.rows[3]!.cells[0]!.actual).toBe('Chewie')
  })

  it('keeps sentence cells on the sentence', () => {
    const view = scenario('Calculator/asserting values')
    const wrong = view.steps.find((s) => s.status === 'failed')!

    expect(cellsOf(wrong.cells, wrong.columns).sentence.map((c) => `${c.name}=${c.actual}`)).toEqual([
      'Sum=8',
      'Product=16',
    ])
  })

  it('prints the keyword the specification wrote, not the kind it maps to', () => {
    const view = scenario('Actions/custom logging from a step')
    expect(view.steps.map((s) => s.kind)).toEqual(['When', 'And', 'Then'])
  })

  it('carries logs, reports and attachments', () => {
    const logged = scenario('Actions/custom logging from a step')
    expect(logged.logs).toEqual(['between steps: 12 rows'])
    expect(logged.steps[1]!.logs).toEqual(['batch started', 'batch finished'])
    expect(logged.reports[0]!.title).toBe('Envelopes')
    expect(cellsOf(logged.reports[0]!.cells, logged.reports[0]!.columns).table!.rows[0]!.cells.map((c) => c?.value)).toEqual([
      'Debit',
      'Succeeded',
    ])

    const thrown = scenario('Calculator/an exception in an action stops the scenario', '../results/')
    expect(thrown.steps[1]!.attachments).toEqual([
      {
        name: 'screen.png',
        mediaType: 'image/png',
        size: 2048,
        href: '../results/attachments/run/spec/01-screen.png',
        omitted: null,
      },
    ])
    expect(thrown.attachments[0]!.href).toBeNull()
    expect(thrown.attachments[0]!.omitted).toBe('over the 10 MB cap')
  })

  it('links no attachment when the results directory was not kept', () => {
    const thrown = scenario('Calculator/an exception in an action stops the scenario', null)
    expect(thrown.steps[1]!.attachments![0]!.href).toBeNull()
  })

  it('reads the verdict, so a failing Acceptance specification is not a failure', () => {
    expect(scenario('Acceptance specifications/a wrong is gathered before the behaviour is finished').status).toBe(
      'acceptance-failing',
    )
    expect(scenario('Actions/custom logging from a step').status).toBe('passed')
    expect(scenario('Calculator/asserting values').status).toBe('failed')
  })
})

describe('a page of results files', () => {
  const page: ResultsPageData = { files: [{ attachmentBase: '', results }] }

  it('groups by feature, then title, and counts what fails the run', () => {
    const features = featuresOf(page)
    expect(features.map((f) => f.feature)).toEqual([
      'Acceptance specifications',
      'Actions',
      'Calculator',
      'Calculator, narrated',
      'Sets',
    ])
    expect(features.find((f) => f.feature === 'Acceptance specifications')!.failing).toBe(0)
    expect(features.find((f) => f.feature === 'Calculator')!.failing).toBe(2)
  })

  it('adds up the summaries of a supervised run, one file per worker', () => {
    const two = summaryOf({ files: [{ attachmentBase: '', results }, { attachmentBase: '', results }] })
    expect(two.scenarios).toBe(12)
    expect(two.failed).toBe(8)
    expect(two.succeeded).toBe(false)
  })

  it('reads the results the tool inlined', () => {
    expect(readPageData(JSON.stringify(page)).data.files[0]!.results.run!.suite).toBe('Bobcat.Xunit.Samples')
  })

  it('reads a bare array of results files, and an empty element as no results', () => {
    expect(readPageData(JSON.stringify([results])).data.files[0]!.attachmentBase).toBeNull()
    expect(readPageData('  ').data.files).toEqual([])
  })

  it('says so when the inlined results cannot be read, rather than rendering nothing', () => {
    const { data, error } = readPageData('{"files": [')
    expect(data.files).toEqual([])
    expect(error).toContain('could not be read')
  })
})
