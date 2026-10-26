/**
 * The run board, grouped by suite (bobcat#786). A flat list of cards, newest first, let one busy
 * suite bury every other one: six identical cards of one suite pushed the rest off the page, and
 * nothing told them apart but their durations. The board is now one row per suite, each expanding to
 * its retained runs — the server keeps the latest few of each suite (`BOBCAT_MONITOR_RETENTION_RUNS`)
 * — so the number of rows is the number of suites on the box, whatever they have been doing.
 *
 * Suite identity is the REPOSITORY plus the suite name: the canonical `owner/name` slug when the
 * server resolved one, else the checkout path, so two worktrees of one repository are one suite and
 * the branch stays a property of each run. The same identity the server's retention counts by.
 */

/** What grouping needs of a run; `RunState` satisfies it. */
export interface GroupableRun {
  runId: string
  suite: string
  repository: string
  startedAt: string
}

/** What grouping needs of an attached runner; `RunnerView` satisfies it. */
export interface GroupableRunner {
  runnerId: string
  suite: string | null
  repository: string
  repositorySlug: string | null
}

export interface SuiteGroup<TRun, TRunner> {
  /** `{repository}::{suite}` — stable across renders, the key a row and its remembered expansion use. */
  key: string
  suite: string
  /** The repository slug, or the checkout path when none resolved. */
  repository: string
  /** The suite's retained runs, newest first. Empty for a suite only a runner has told the board about. */
  runs: TRun[]
  /** The runs still being published to, newest first. */
  live: TRun[]
  /** The newest run, live or not; undefined when nothing has run. */
  latest: TRun | undefined
  /** The attached runner for this suite, when one is. */
  runner: TRunner | undefined
}

/** Backslashes and a trailing slash are not a different checkout. */
export function normalizePath(path: string | null | undefined): string {
  return (path ?? '').replace(/\\/g, '/').replace(/\/+$/, '')
}

/** The identity two runs (or a run and a runner) share when they are the same suite. */
export function suiteKey(repository: string, suite: string): string {
  return `${normalizePath(repository)}::${suite}`
}

/**
 * Group the runs by suite and attach each suite's runner (bobcat#645: a runner with no runs yet is
 * still a suite on the board, with its Run button, or a person has nothing to press until a
 * `dotnet test` has made a card). Live suites sort first — by their newest run — then the rest by
 * most recent run, then runner-only suites by name. Within a suite, newest first.
 */
export function groupBySuite<TRun extends GroupableRun, TRunner extends GroupableRunner>(
  runs: readonly TRun[],
  runners: readonly TRunner[] | null,
  repositoryOf: (run: TRun) => string | null | undefined,
  isLive: (run: TRun) => boolean,
): SuiteGroup<TRun, TRunner>[] {
  const groups = new Map<string, SuiteGroup<TRun, TRunner>>()

  const groupFor = (repository: string, suite: string) => {
    const key = suiteKey(repository, suite)
    let group = groups.get(key)
    if (!group) {
      group = { key, suite, repository, runs: [], live: [], latest: undefined, runner: undefined }
      groups.set(key, group)
    }
    return group
  }

  for (const run of runs) {
    groupFor(repositoryOf(run) ?? run.repository, run.suite).runs.push(run)
  }

  for (const runner of runners ?? []) {
    if (!runner.suite) continue
    const group = groupFor(runner.repositorySlug ?? runner.repository, runner.suite)
    // One runner per suite is the normal case; a second (two checkouts of one repository, say) is
    // reached from the Run dialog, which lists them all.
    group.runner ??= runner
  }

  const started = (run: TRun) => Date.parse(run.startedAt) || 0
  for (const group of groups.values()) {
    group.runs.sort((a, b) => started(b) - started(a))
    group.live = group.runs.filter(isLive)
    group.latest = group.runs[0]
  }

  return [...groups.values()].sort((a, b) => {
    const aLive = a.live.length > 0 ? 1 : 0
    const bLive = b.live.length > 0 ? 1 : 0
    if (aLive !== bLive) return bLive - aLive
    const aLatest = a.latest ? started(a.latest) : -1
    const bLatest = b.latest ? started(b.latest) : -1
    if (aLatest !== bLatest) return bLatest - aLatest
    return a.suite.localeCompare(b.suite) || a.repository.localeCompare(b.repository)
  })
}

/** The history strip's length, and how many cards a suite shows before "and N older". */
export const HISTORY_LENGTH = 5

/** What a finished run's verdict needs; `RunState` satisfies it. */
export interface VerdictRun {
  finished: boolean
  orphaned: boolean
  exitCode: number | null
  counts: { failed: number; indeterminate: number; passedOnRetry: number } | null
}

export type VerdictTone = 'live' | 'passed' | 'retried' | 'failed' | 'orphaned' | 'unknown'

/**
 * One word for a run (bobcat#786), the colour of its history segment and of its row's latest
 * result. An indeterminate outcome is a failure here, as it is in the supervisor's exit code; a
 * pass on retry is its own tone, never folded into a clean pass.
 */
export function verdictToneOf(run: VerdictRun): VerdictTone {
  if (run.orphaned) return 'orphaned'
  if (!run.finished) return 'live'
  if (!run.counts) return run.exitCode === 0 ? 'passed' : run.exitCode === null ? 'unknown' : 'failed'
  if (run.counts.failed > 0 || run.counts.indeterminate > 0) return 'failed'
  if (run.counts.passedOnRetry > 0) return 'retried'
  return 'passed'
}

/**
 * The suites a viewer has expanded, remembered in their browser (the only per-viewer board setting
 * left: the global "Show N" went with the flat list). The pre-#524 key `stoat.runs.limit` is removed
 * when found — the setting it stored no longer exists.
 */
const EXPANDED_KEY = 'bobcat.board.expanded'
const LEGACY_LIMIT_KEY = 'stoat.runs.limit'

export function loadExpandedSuites(): Set<string> {
  try {
    localStorage.removeItem(LEGACY_LIMIT_KEY)
    const stored = localStorage.getItem(EXPANDED_KEY)
    const parsed: unknown = stored ? JSON.parse(stored) : []
    return new Set(Array.isArray(parsed) ? parsed.filter((k): k is string => typeof k === 'string') : [])
  } catch {
    return new Set()
  }
}

export function saveExpandedSuites(keys: ReadonlySet<string>): void {
  try {
    localStorage.setItem(EXPANDED_KEY, JSON.stringify([...keys]))
  } catch {
    // A viewer whose browser will not remember simply finds the rows collapsed next time.
  }
}

/** "keeps 5 runs per suite · 14 days", from what the console reported it is configured with. */
export function describeRetention(policy: { runs: number; days: number } | null): string | null {
  if (!policy) return null
  const runs = policy.runs > 0 ? `keeps ${policy.runs} run${policy.runs === 1 ? '' : 's'} per suite` : 'keeps every run'
  const days = policy.days > 0 ? `${formatDays(policy.days)} of archive` : 'archive kept indefinitely'
  return `${runs} · ${days}`
}

function formatDays(days: number): string {
  const rounded = Number.isInteger(days) ? days : Math.round(days * 10) / 10
  return `${rounded} day${rounded === 1 ? '' : 's'}`
}
