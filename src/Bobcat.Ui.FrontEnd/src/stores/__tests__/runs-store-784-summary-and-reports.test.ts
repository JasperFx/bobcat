import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useRunsStore } from '../runs-store'

/**
 * bobcat#784 — what the run state carries that it dropped before: the tag, session and command a
 * run was launched with, and the scenario's reports. Both ways in: the live run_started, and the
 * board summary.
 */
const RUN = '11111111-1111-1111-1111-111111111784'

beforeEach(() => setActivePinia(createPinia()))

describe('runs-store — tag, session and command', () => {
  it('reach the run state from run_started', () => {
    const store = useRunsStore()
    store.handleRunStarted({
      runId: RUN, suite: 'S', repository: '/repo', branch: 'main', mode: 'in-process', startedAt: '2026-10-26T10:00:00Z',
      totalScenarios: 1, tag: 'plan/node-7', session: 's-abc', command: '6f1c7a64-1b1e-4c55-9d1a-2f5b8e0a9c11',
    })

    const run = store.runById(RUN)!
    expect([run.tag, run.session, run.command]).toEqual(['plan/node-7', 's-abc', '6f1c7a64-1b1e-4c55-9d1a-2f5b8e0a9c11'])
    expect(run.eventsLoaded).toBe(true)
  })

  it('are null from a publisher that sent none', () => {
    const store = useRunsStore()
    store.handleRunStarted({ runId: RUN, suite: 'S', repository: '/repo', branch: null, mode: 'in-process', startedAt: '2026-10-26T10:00:00Z', totalScenarios: null })

    expect(store.runById(RUN)).toMatchObject({ tag: null, session: null, command: null })
  })

  it('reach the run state from the board summary, with the counts and the orphan flag, and never its events', () => {
    const store = useRunsStore()
    store.applySummaries([{
      runId: RUN, suite: 'S', repository: '/repo', repositorySlug: 'JasperFx/repo', branch: 'main', mode: 'supervised',
      finished: true, orphaned: false, exitCode: 1, scenarios: 3, startedAt: '2026-10-26T10:00:00Z', finishedAt: '2026-10-26T10:05:00Z',
      tag: 'plan/node-7', session: null, command: 'cmd-1', totalScenarios: 3, scenariosFinished: 3,
      passed: 2, failed: 1, passedOnRetry: 1, indeterminate: 0, skipped: 1, lastEventAt: '2026-10-26T10:05:00Z',
    }])

    const run = store.runById(RUN)!
    expect(run).toMatchObject({
      suite: 'S', mode: 'supervised', finished: true, exitCode: 1, finishedAt: '2026-10-26T10:05:00Z',
      tag: 'plan/node-7', session: null, command: 'cmd-1', totalScenarios: 3, lastEventAt: '2026-10-26T10:05:00Z', eventsLoaded: false,
    })
    expect(run.counts).toEqual({ passed: 2, failed: 1, passedOnRetry: 1, indeterminate: 0, skipped: 1 })
    expect(Object.keys(run.scenarios)).toHaveLength(0)
    expect(store.repositoryOf(run)).toBe('JasperFx/repo')
  })

  it('a summary refresh keeps the events a page already loaded', () => {
    const store = useRunsStore()
    store.handleRunStarted({ runId: RUN, suite: 'S', repository: '/repo', branch: null, mode: 'in-process', startedAt: '2026-10-26T10:00:00Z', totalScenarios: 1 })
    store.handleScenarioStarted({ runId: RUN, uid: 'F/a', feature: 'F', scenario: 'a', attempt: 1, at: '2026-10-26T10:00:01Z' })

    store.applySummaries([{
      runId: RUN, suite: 'S', repository: '/repo', branch: null, mode: 'in-process', finished: false, orphaned: true,
      exitCode: null, scenarios: 1, startedAt: '2026-10-26T10:00:00Z', finishedAt: null,
    }])

    const run = store.runById(RUN)!
    expect(run.eventsLoaded).toBe(true)
    expect(run.scenarios['F/a']).toBeDefined()
    expect(run.orphaned).toBe(true)
    expect(run.counts).toBeNull()
  })
})

describe('runs-store — a scenario’s reports (bobcat#408)', () => {
  const finished = (reports?: unknown[]) => ({
    runId: RUN, uid: 'F/a', outcome: 'Failed', attempts: 1, durationMs: 5, errorMessage: 'x',
    reports: reports as never,
  })

  it('folds the reports from scenario_finished, whole, so a replay lands the same grids', () => {
    const store = useRunsStore()
    store.handleScenarioStarted({ runId: RUN, uid: 'F/a', feature: 'F', scenario: 'a', attempt: 1, at: '2026-10-26T10:00:01Z' })
    const event = finished([{
      title: 'Message activity', shortTitle: 'Activity', columns: ['Message', 'Outcome'], suppressedRows: 2,
      cells: [
        { name: 'Message', status: 'ok', expected: null, actual: null, note: null, rowIndex: 0, value: 'CreditWallet', comparison: null },
        { name: 'Outcome', status: 'ok', expected: null, actual: null, note: null, rowIndex: 0, value: 'handled', comparison: null },
      ],
    }])
    store.handleScenarioFinished(event)
    store.handleScenarioFinished(event)

    const reports = store.runById(RUN)!.scenarios['F/a']!.reports
    expect(reports).toHaveLength(1)
    expect(reports[0]).toMatchObject({ title: 'Message activity', shortTitle: 'Activity', columns: ['Message', 'Outcome'], suppressedRows: 2 })
    expect(reports[0]!.cells.map((c) => c.value)).toEqual(['CreditWallet', 'handled'])
  })

  it('leaves the reports empty from a publisher that sent none, and keeps them through a finish that says nothing', () => {
    const store = useRunsStore()
    store.handleScenarioStarted({ runId: RUN, uid: 'F/a', feature: 'F', scenario: 'a', attempt: 1, at: '2026-10-26T10:00:01Z' })
    store.handleScenarioFinished(finished())
    expect(store.runById(RUN)!.scenarios['F/a']!.reports).toEqual([])

    store.handleScenarioFinished(finished([{ title: 'Events', columns: [], cells: [], suppressedRows: 0 }]))
    store.handleScenarioFinished(finished())
    expect(store.runById(RUN)!.scenarios['F/a']!.reports.map((r) => r.title)).toEqual(['Events'])
  })
})
