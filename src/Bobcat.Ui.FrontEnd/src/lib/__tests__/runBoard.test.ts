import { afterEach, describe, expect, it } from 'vitest'
import {
  describeRetention,
  groupBySuite,
  loadExpandedSuites,
  saveExpandedSuites,
  suiteKey,
  verdictToneOf,
  type GroupableRun,
  type GroupableRunner,
} from '../runBoard'

/**
 * bobcat#786 — the board is grouped by suite identity (repository + suite), live suites first and
 * then by most recent run. The observed case: six identical cards of one busy suite pushed every
 * other suite off the page, told apart only by their durations.
 */
type Run = GroupableRun & { live?: boolean }

const run = (runId: string, suite: string, repository: string, startedAt: string, live = false): Run => ({
  runId, suite, repository, startedAt, live,
})
const runner = (runnerId: string, suite: string, repository: string, repositorySlug: string | null = null): GroupableRunner => ({
  runnerId, suite, repository, repositorySlug,
})

const slugs: Record<string, string | null> = {
  '/code/bobcat': 'JasperFx/bobcat',
  '/code/bobcat-wt': 'JasperFx/bobcat',
  '/code/stoat': 'JasperFx/stoat',
  '/tmp/scratch': null,
}
const repositoryOf = (r: Run) => slugs[r.repository]
const isLive = (r: Run) => r.live === true

function group(runs: Run[], runners: GroupableRunner[] | null = null) {
  return groupBySuite(runs, runners, repositoryOf, isLive)
}

describe('groupBySuite', () => {
  it('puts two worktrees of one repository under one suite, and keeps the branch to the run', () => {
    const groups = group([
      run('a', 'Bobcat.Tests', '/code/bobcat', '2026-10-26T10:00:00Z'),
      run('b', 'Bobcat.Tests', '/code/bobcat-wt', '2026-10-26T11:00:00Z'),
    ])
    expect(groups).toHaveLength(1)
    expect(groups[0]!.repository).toBe('JasperFx/bobcat')
    expect(groups[0]!.runs.map((r) => r.runId)).toEqual(['b', 'a'])
    expect(groups[0]!.latest?.runId).toBe('b')
  })

  it('tells the same suite name in two repositories apart, and a run nothing resolved keeps its path', () => {
    const groups = group([
      run('a', 'Specs', '/code/bobcat', '2026-10-26T10:00:00Z'),
      run('b', 'Specs', '/code/stoat', '2026-10-26T10:00:00Z'),
      run('c', 'Specs', '/tmp/scratch', '2026-10-26T10:00:00Z'),
    ])
    expect(groups.map((g) => g.repository).sort()).toEqual(['/tmp/scratch', 'JasperFx/bobcat', 'JasperFx/stoat'])
    expect(groups.find((g) => g.repository === '/tmp/scratch')!.key).toBe(suiteKey('/tmp/scratch', 'Specs'))
  })

  it('sorts live suites first, then the rest by their most recent run', () => {
    const groups = group([
      run('old-live', 'Slow.Gate', '/code/bobcat', '2026-10-26T08:00:00Z', true),
      run('newest', 'Quick.Tests', '/code/bobcat', '2026-10-26T12:00:00Z'),
      run('middle', 'Other.Tests', '/code/stoat', '2026-10-26T11:00:00Z'),
      run('older', 'Quick.Tests', '/code/bobcat', '2026-10-26T09:00:00Z'),
    ])
    expect(groups.map((g) => g.suite)).toEqual(['Slow.Gate', 'Quick.Tests', 'Other.Tests'])
    expect(groups[0]!.live.map((r) => r.runId)).toEqual(['old-live'])
    expect(groups[1]!.runs.map((r) => r.runId)).toEqual(['newest', 'older'])
  })

  it('makes a row for a runner whose suite has not run yet, after the suites that have', () => {
    const groups = group(
      [run('a', 'Bobcat.Tests', '/code/bobcat', '2026-10-26T10:00:00Z')],
      [runner('r1', 'Bank.Specs', '/code/bank', 'JasperFx/bank'), runner('r2', 'Bobcat.Tests', '/code/bobcat-wt', 'JasperFx/bobcat')],
    )
    expect(groups.map((g) => g.suite)).toEqual(['Bobcat.Tests', 'Bank.Specs'])
    expect(groups[0]!.runner?.runnerId).toBe('r2')
    expect(groups[1]!.runs).toEqual([])
    expect(groups[1]!.latest).toBeUndefined()
    expect(groups[1]!.runner?.runnerId).toBe('r1')
  })

  it('attaches a runner by its slug, or by its path when it has none', () => {
    const groups = group(
      [run('a', 'Scratch', '/tmp/scratch', '2026-10-26T10:00:00Z')],
      [runner('r1', 'Scratch', '/tmp/scratch/')],
    )
    expect(groups).toHaveLength(1)
    expect(groups[0]!.runner?.runnerId).toBe('r1')
  })

  it('ignores a runner that registered no suite', () => {
    const groups = group([], [{ runnerId: 'r', suite: null, repository: '/x', repositorySlug: null }])
    expect(groups).toEqual([])
  })
})

describe('verdictToneOf', () => {
  const finished = { finished: true, orphaned: false, exitCode: 0 }
  it('reads a run the way its history segment is coloured', () => {
    expect(verdictToneOf({ ...finished, counts: { failed: 0, indeterminate: 0, passedOnRetry: 0 } })).toBe('passed')
    expect(verdictToneOf({ ...finished, counts: { failed: 0, indeterminate: 0, passedOnRetry: 2 } })).toBe('retried')
    expect(verdictToneOf({ ...finished, counts: { failed: 1, indeterminate: 0, passedOnRetry: 2 } })).toBe('failed')
    // An indeterminate outcome is a failure, as it is in the supervisor's exit code.
    expect(verdictToneOf({ ...finished, counts: { failed: 0, indeterminate: 1, passedOnRetry: 0 } })).toBe('failed')
    expect(verdictToneOf({ finished: false, orphaned: false, exitCode: null, counts: null })).toBe('live')
    expect(verdictToneOf({ finished: false, orphaned: true, exitCode: null, counts: null })).toBe('orphaned')
    expect(verdictToneOf({ ...finished, exitCode: 1, counts: null })).toBe('failed')
  })
})

describe('the remembered expansions', () => {
  afterEach(() => localStorage.clear())

  it('remembers which suites are open, under a bobcat key', () => {
    saveExpandedSuites(new Set(['JasperFx/bobcat::Bobcat.Tests']))
    expect(localStorage.getItem('bobcat.board.expanded')).toBe('["JasperFx/bobcat::Bobcat.Tests"]')
    expect([...loadExpandedSuites()]).toEqual(['JasperFx/bobcat::Bobcat.Tests'])
  })

  it('removes the pre-#524 "Show N" key, whose setting no longer exists', () => {
    localStorage.setItem('stoat.runs.limit', '10')
    expect([...loadExpandedSuites()]).toEqual([])
    expect(localStorage.getItem('stoat.runs.limit')).toBeNull()
  })

  it('ignores a stored value that is not a list of keys', () => {
    localStorage.setItem('bobcat.board.expanded', '{"not":"a list"}')
    expect([...loadExpandedSuites()]).toEqual([])
  })
})

describe('describeRetention', () => {
  it('states what the console keeps, and says when a bound is off', () => {
    expect(describeRetention({ runs: 5, days: 14 })).toBe('keeps 5 runs per suite · 14 days of archive')
    expect(describeRetention({ runs: 1, days: 1 })).toBe('keeps 1 run per suite · 1 day of archive')
    expect(describeRetention({ runs: 0, days: 0 })).toBe('keeps every run · archive kept indefinitely')
    expect(describeRetention(null)).toBeNull()
  })
})
