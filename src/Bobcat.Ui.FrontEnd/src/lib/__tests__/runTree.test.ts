import { describe, expect, it } from 'vitest'
import {
  buildRunTree,
  filterTree,
  rollup,
  runningScenario,
  startsExpanded,
  summarize,
  tocStatusOf,
} from '../runTree'
import type { ScenarioState, ScenarioStatus } from '@/stores/runs-store'

/**
 * bobcat#787 — the run page's table of contents: the suite tree with a status on every node and
 * rollup counts per feature, the summary tiles, and what "Go to running" goes to. Pure functions
 * over the store's scenarios.
 */
function scenario(uid: string, status: ScenarioStatus, overrides: Partial<ScenarioState> = {}): ScenarioState {
  const slash = uid.indexOf('/')
  return {
    uid,
    feature: slash > 0 ? uid.slice(0, slash) : '',
    scenario: slash > 0 ? uid.slice(slash + 1) : uid,
    status,
    attempt: 1,
    attempts: null,
    scheduledAttempt: null,
    outcome: null,
    durationMs: null,
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
    reports: [],
    ...overrides,
  }
}

describe('tocStatusOf', () => {
  it('folds the store’s seven statuses into the rail’s four', () => {
    expect(tocStatusOf('running')).toBe('running')
    expect(tocStatusOf('retry-scheduled')).toBe('running')
    expect(tocStatusOf('failed')).toBe('failed')
    expect(tocStatusOf('aborted')).toBe('failed')
    expect(tocStatusOf('acceptance-failing')).toBe('failed')
    expect(tocStatusOf('passed')).toBe('passed')
    expect(tocStatusOf('passed-on-retry')).toBe('passed')
  })
})

describe('rollup', () => {
  it('is running over failed over pending over passed, and pending when empty', () => {
    expect(rollup({ running: 1, failed: 1, pending: 1, passed: 1 })).toBe('running')
    expect(rollup({ running: 0, failed: 1, pending: 1, passed: 1 })).toBe('failed')
    expect(rollup({ running: 0, failed: 0, pending: 1, passed: 1 })).toBe('pending')
    expect(rollup({ running: 0, failed: 0, pending: 0, passed: 1 })).toBe('passed')
    expect(rollup({ running: 0, failed: 0, pending: 0, passed: 0 })).toBe('pending')
  })
})

describe('buildRunTree', () => {
  it('groups scenarios under their features in first-seen order, with a rollup per feature', () => {
    const tree = buildRunTree([
      scenario('Wallet/credit', 'passed'),
      scenario('Orders/place', 'failed'),
      scenario('Wallet/debit', 'running'),
      scenario('Wallet/close', 'passed-on-retry'),
    ])

    expect(tree.features.map((f) => f.name)).toEqual(['Wallet', 'Orders'])
    const wallet = tree.features[0]!
    expect(wallet.scenarios.map((s) => s.title)).toEqual(['credit', 'debit', 'close'])
    expect(wallet.counts).toEqual({ running: 1, failed: 0, pending: 0, passed: 2 })
    expect(wallet.status).toBe('running')
    expect(wallet.anchor).toBe('feature:Wallet')

    const orders = tree.features[1]!
    expect(orders.status).toBe('failed')
    expect(orders.scenarios[0]).toEqual({ uid: 'Orders/place', title: 'place', status: 'failed', scenarioStatus: 'failed' })

    expect(tree.counts).toEqual({ running: 1, failed: 1, pending: 0, passed: 2 })
    expect(tree.status).toBe('running')
  })

  it('puts the specifications a command named but the run has not reached on the rail as pending', () => {
    const tree = buildRunTree([scenario('Wallet/credit', 'passed')], ['Wallet/credit', 'Wallet/debit', 'Orders/place'])

    expect(tree.features.map((f) => f.name)).toEqual(['Wallet', 'Orders'])
    expect(tree.features[0]!.scenarios.map((s) => [s.title, s.status])).toEqual([
      ['credit', 'passed'],
      ['debit', 'pending'],
    ])
    expect(tree.features[0]!.scenarios[1]!.scenarioStatus).toBeNull()
    expect(tree.features[1]!.status).toBe('pending')
    expect(tree.counts.pending).toBe(2)
  })

  it('keeps a forwarded foreign test, which has no feature, under a nameless feature', () => {
    const tree = buildRunTree([scenario('Ns.Class.method', 'passed', { forwarded: true, displayName: 'Ns.Class.method' })])
    expect(tree.features[0]!.name).toBe('')
    expect(tree.features[0]!.anchor).toBe('feature:(none)')
    expect(tree.features[0]!.scenarios[0]!.title).toBe('Ns.Class.method')
  })

  it('an empty run is a pending tree', () => {
    expect(buildRunTree([]).status).toBe('pending')
  })
})

describe('filterTree', () => {
  const tree = buildRunTree([
    scenario('Wallet/credit', 'passed'),
    scenario('Wallet/debit', 'failed'),
    scenario('Orders/place', 'passed'),
  ])

  it('narrows every feature to one status and drops the features with nothing left', () => {
    const failed = filterTree(tree, 'failed')
    expect(failed.features.map((f) => f.name)).toEqual(['Wallet'])
    expect(failed.features[0]!.scenarios.map((s) => s.title)).toEqual(['debit'])
    // The rollup is the feature's whole story, not the filtered view's.
    expect(failed.features[0]!.counts).toEqual(tree.features[0]!.counts)
    expect(failed.counts).toEqual(tree.counts)
  })

  it('null is no filter', () => {
    expect(filterTree(tree, null)).toBe(tree)
  })
})

describe('summarize', () => {
  it('counts the scenarios on hand while the run is going', () => {
    const summary = summarize({ counts: null, finished: false, totalScenarios: 6 }, [
      scenario('W/a', 'passed'),
      scenario('W/b', 'passed-on-retry'),
      scenario('W/c', 'failed'),
      scenario('W/d', 'running'),
      scenario('W/e', 'retry-scheduled'),
    ])
    expect(summary).toEqual({ passed: 1, passedOnRetry: 1, failed: 1, running: 2, pending: 1, acceptanceFailing: 0 })
  })

  it('“no result yet” is the declared total less what was heard of, or the command’s unreached specs', () => {
    const declared = summarize({ counts: null, finished: false, totalScenarios: 10 }, [scenario('W/a', 'passed')])
    expect(declared.pending).toBe(9)

    const commanded = summarize({ counts: null, finished: false, totalScenarios: null }, [scenario('W/a', 'passed')], ['W/a', 'W/b', 'W/c'])
    expect(commanded.pending).toBe(2)

    // Both known: the larger, never a sum of the same specs twice.
    const both = summarize({ counts: null, finished: false, totalScenarios: 3 }, [scenario('W/a', 'passed')], ['W/a', 'W/b', 'W/c'])
    expect(both.pending).toBe(2)
  })

  it('a finished run’s terminal counts win, with indeterminate counted as failed and nothing pending', () => {
    const summary = summarize(
      { counts: { passed: 18, failed: 1, passedOnRetry: 2, indeterminate: 1, skipped: null }, finished: true, totalScenarios: 22 },
      [scenario('W/a', 'passed')],
    )
    expect(summary).toEqual({ passed: 18, passedOnRetry: 2, failed: 2, running: 0, pending: 0, acceptanceFailing: 0 })
  })

  it('an acceptance spec not yet passing is counted apart, never as failed', () => {
    const summary = summarize({ counts: null, finished: false, totalScenarios: null }, [scenario('W/a', 'acceptance-failing')])
    expect(summary.failed).toBe(0)
    expect(summary.acceptanceFailing).toBe(1)
  })
})

describe('runningScenario', () => {
  it('is the first scenario in flight, or null', () => {
    expect(runningScenario([scenario('W/a', 'passed'), scenario('W/b', 'running'), scenario('W/c', 'running')])?.uid).toBe('W/b')
    expect(runningScenario([scenario('W/a', 'passed')])).toBeNull()
  })
})

describe('startsExpanded', () => {
  it('folds a passed scenario and opens everything still worth reading', () => {
    expect(startsExpanded('passed')).toBe(false)
    expect(startsExpanded('passed-on-retry')).toBe(false)
    expect(startsExpanded('failed')).toBe(true)
    expect(startsExpanded('aborted')).toBe(true)
    expect(startsExpanded('running')).toBe(true)
    expect(startsExpanded('retry-scheduled')).toBe(true)
    expect(startsExpanded('acceptance-failing')).toBe(true)
  })
})
