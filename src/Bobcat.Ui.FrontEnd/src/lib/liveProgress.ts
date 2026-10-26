import { visibleScenarios, type RunState, type ScenarioState } from '@/stores/runs-store'
import { commandForRun, describeCommand, type RunnerView } from '@/lib/runners'

/**
 * A live run's overall progress (bobcat#786): how far through its planned total it is, what has
 * passed and failed so far, how long it has run and roughly how long it has left. One computation,
 * read by the suite row's bar and the status strip's mini bar, so the two never disagree about the
 * same run. Before this a live card showed a neutral bar with no position, no counts and no ETA — on
 * a 3,000-test suite across six lanes, a bar that said nothing for twenty minutes.
 */
export interface LiveCounts {
  /** Scenarios with a verdict so far, retries folded into one each. */
  done: number
  passed: number
  failed: number
  /** Scenarios that have passed on a retry or are waiting on one — the amber signal. */
  retried: number
  /** Scenarios in flight right now. */
  running: number
}

/** Green while everything passes, red at the first failure, amber when only retries have happened. */
export type ProgressTone = 'green' | 'amber' | 'red'

export function toneOf(counts: Pick<LiveCounts, 'failed' | 'retried'>): ProgressTone {
  if (counts.failed > 0) return 'red'
  if (counts.retried > 0) return 'amber'
  return 'green'
}

const FAILED: ReadonlySet<ScenarioState['status']> = new Set(['failed', 'aborted', 'acceptance-failing'])

/** The verdicts a run has collected so far, from the scenarios the board has heard. */
export function countScenarios(run: RunState): LiveCounts {
  const counts: LiveCounts = { done: 0, passed: 0, failed: 0, retried: 0, running: 0 }
  for (const scenario of visibleScenarios(run)) {
    switch (scenario.status) {
      case 'running':
        counts.running++
        break
      case 'retry-scheduled':
        counts.retried++
        break
      case 'passed-on-retry':
        counts.done++
        counts.passed++
        counts.retried++
        break
      case 'passed':
        counts.done++
        counts.passed++
        break
      default:
        if (FAILED.has(scenario.status)) {
          counts.done++
          counts.failed++
        }
    }
  }
  return counts
}

/**
 * How many scenarios the run will take, best source first: the run's own declared total; the
 * supervisor's heartbeat total (a foreign suite declares none); what the runner says — the specs a
 * command selected, else everything it registered (`plannedHintOf`); the suite's last finished run.
 * Null when nobody can say — the bar is then indeterminate rather than a guess drawn as a fact.
 */
export function plannedTotalOf(
  run: RunState,
  plannedHint: number | null | undefined,
  previousRuns: readonly RunState[],
): number | null {
  if (run.totalScenarios && run.totalScenarios > 0) return run.totalScenarios
  if (run.progress && run.progress.total > 0) return run.progress.total
  if (plannedHint && plannedHint > 0) return plannedHint
  for (const previous of previousRuns) {
    const total = scenariosOf(previous)
    if (total > 0) return total
  }
  return null
}

/**
 * The runner's word on the total (bobcat#646): a commanded run runs the specifications its command
 * selected — two, not the runner's twenty-two — and an uncommanded run of a suite with a runner
 * attached is read as the whole project. Null without a runner.
 */
export function plannedHintOf(run: RunState, runners: readonly RunnerView[] | null): number | null {
  const commanded = commandForRun(runners as RunnerView[] | null, run.runId)
  if (commanded) return commanded.command.specs.length > 0 ? commanded.command.specs.length : commanded.runner.specs
  const runner = (runners ?? []).find((r) => r.suite === run.suite)
  return runner && runner.specs > 0 ? runner.specs : null
}

/**
 * What a commanded run was asked to run (bobcat#646), and while it is in flight with no declared
 * total, how far through its selection it is: the runner knows how many specifications it selected,
 * while the run itself publishes no total. Null for a run nobody commanded.
 */
export function commandLineOf(run: RunState, runners: readonly RunnerView[] | null): string | null {
  const found = commandForRun(runners as RunnerView[] | null, run.runId)
  if (!found) return null
  const line = describeCommand(found.command, found.runner)
  if (run.finished || run.orphaned || run.totalScenarios) return line
  const done = countScenarios(run).done
  const specs = found.command.specs.length
  return `${line} · ${done} finished of ${specs} spec${specs === 1 ? '' : 's'} selected`
}

/** How many scenarios a finished run had, from its counts, else from the scenarios it holds. */
function scenariosOf(run: RunState): number {
  if (run.counts) {
    return run.counts.passed + run.counts.failed + run.counts.indeterminate + (run.counts.skipped ?? 0)
  }
  return visibleScenarios(run).length
}

/**
 * The durations of this suite's previous finished runs, in milliseconds, for the estimate. An
 * orphaned or unfinished run has no duration to offer.
 */
export function previousDurationsOf(previousRuns: readonly RunState[]): number[] {
  const durations: number[] = []
  for (const run of previousRuns) {
    if (!run.finished || !run.finishedAt) continue
    const ms = Date.parse(run.finishedAt) - Date.parse(run.startedAt)
    if (Number.isFinite(ms) && ms > 0) durations.push(ms)
  }
  return durations
}

/**
 * Milliseconds left, or null when nothing supports an estimate. Previous runs' durations are the
 * better source — the median of them, less what has elapsed, so one slow outlier does not double
 * the figure — and a suite with no history is extrapolated from its own pace once anything has
 * finished. Never negative: a run past its estimate reads as "any moment", not a countdown below zero.
 */
export function etaMs(
  elapsedMs: number,
  done: number,
  planned: number | null,
  previousDurationsMs: readonly number[],
): number | null {
  if (previousDurationsMs.length > 0) {
    const sorted = [...previousDurationsMs].sort((a, b) => a - b)
    const middle = Math.floor(sorted.length / 2)
    const median = sorted.length % 2 === 1 ? sorted[middle]! : (sorted[middle - 1]! + sorted[middle]!) / 2
    return Math.max(0, median - elapsedMs)
  }
  if (planned && planned > 0 && done > 0 && elapsedMs > 0) {
    const remaining = Math.max(0, planned - done)
    return Math.round((elapsedMs / done) * remaining)
  }
  return null
}

/** "about 2m", "about 40s", "any moment" — coarse, because an estimate drawn to the second lies. */
export function formatEta(ms: number | null): string | null {
  if (ms === null) return null
  if (ms < 5_000) return 'any moment'
  if (ms < 60_000) return `about ${Math.round(ms / 5_000) * 5}s`
  if (ms < 3_600_000) return `about ${Math.round(ms / 60_000)}m`
  const hours = Math.floor(ms / 3_600_000)
  const minutes = Math.round((ms % 3_600_000) / 60_000)
  return `about ${hours}h${minutes > 0 ? ` ${minutes}m` : ''}`
}

/** "1m05s" style, for the elapsed clock — the same shape `formatDuration` gives a finished run. */
export function formatElapsed(ms: number): string {
  const clamped = Math.max(0, ms)
  if (clamped < 60_000) return `${Math.floor(clamped / 1000)}s`
  const minutes = Math.floor(clamped / 60_000)
  const seconds = Math.floor((clamped % 60_000) / 1000)
  if (minutes < 60) return `${minutes}m${seconds.toString().padStart(2, '0')}s`
  return `${Math.floor(minutes / 60)}h${(minutes % 60).toString().padStart(2, '0')}m`
}

export interface LiveProgress {
  counts: LiveCounts
  planned: number | null
  /** done / planned in [0, 1]; null while the total is unknown. */
  fraction: number | null
  tone: ProgressTone
  elapsedMs: number
  etaMs: number | null
}

/** Everything the bars show about one live run, as of `now`. */
export function liveProgressOf(
  run: RunState,
  now: number,
  plannedHint: number | null | undefined,
  previousRuns: readonly RunState[],
): LiveProgress {
  const counts = countScenarios(run)
  const planned = plannedTotalOf(run, plannedHint, previousRuns)
  const elapsedMs = Math.max(0, now - (Date.parse(run.startedAt) || now))
  return {
    counts,
    planned,
    fraction: planned ? Math.min(1, counts.done / planned) : null,
    tone: toneOf(counts),
    elapsedMs,
    etaMs: etaMs(elapsedMs, counts.done, planned, previousDurationsOf(previousRuns)),
  }
}
