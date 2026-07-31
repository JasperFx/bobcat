import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import type {
  RetryScheduled,
  RunFinished,
  RunHeartbeat,
  RunStarted,
  ScenarioFinished,
  ScenarioStarted,
  StepFinished,
  StepStarted,
} from '@/messages/monitor-events'

export type StepStatus = 'running' | 'passed' | 'failed'

export interface StepState {
  stepId: string
  kind: string
  text: string
  status: StepStatus
  durationMs: number | null
  errorMessage: string | null
}

export type ScenarioStatus = 'running' | 'passed' | 'passed-on-retry' | 'failed' | 'retry-scheduled' | 'aborted'

export interface ScenarioState {
  uid: string
  feature: string
  scenario: string
  status: ScenarioStatus
  /** 1-based attempt currently running (or the last one that ran). */
  attempt: number
  /** Total attempts reported by the terminal ScenarioFinished, if any. */
  attempts: number | null
  outcome: string | null
  durationMs: number | null
  errorMessage: string | null
  retryReason: string | null
  /** Steps of the CURRENT attempt only — a retry starts a fresh bracket. */
  steps: StepState[]
}

export interface RunCounts {
  passed: number
  failed: number
  passedOnRetry: number
  indeterminate: number
}

export interface RunState {
  runId: string
  suite: string
  repository: string
  branch: string | null
  mode: string
  startedAt: string
  totalScenarios: number | null
  finished: boolean
  exitCode: number | null
  counts: RunCounts | null
  finishedAt: string | null
  /** Wall-clock of the latest event/heartbeat — the orphaned-run detector's input. */
  lastEventAt: string
  scenarios: Record<string, ScenarioState>
}

/**
 * All live and recently-finished runs on this box, keyed by runId. Every mutation
 * arrives as a MonitorEvent via relayToStore; the store is deliberately tolerant of
 * out-of-order or missing events (a publisher may attach mid-run, drop batches on
 * backpressure, or crash), so handlers upsert rather than assume prior state.
 */
export const useRunsStore = defineStore('runs', () => {
  const runs = ref<Record<string, RunState>>({})

  const allRuns = computed(() => Object.values(runs.value))
  const activeRuns = computed(() => allRuns.value.filter((r) => !r.finished))
  const finishedRuns = computed(() => allRuns.value.filter((r) => r.finished))

  function runById(runId: string): RunState | undefined {
    return runs.value[runId]
  }

  /** Completed scenarios / total, in [0, 1]; null when the total is unknown. */
  function progressOf(run: RunState): number | null {
    if (!run.totalScenarios || run.totalScenarios <= 0) return null
    const done = Object.values(run.scenarios).filter(
      (s) => s.status !== 'running' && s.status !== 'retry-scheduled',
    ).length
    return Math.min(1, done / run.totalScenarios)
  }

  function ensureRun(runId: string, at?: string): RunState {
    let run = runs.value[runId]
    if (!run) {
      // Events arrived before (or without) run_started — synthesize a shell so
      // nothing is dropped; run_started will fill in the metadata if it comes.
      run = {
        runId,
        suite: '(unknown suite)',
        repository: '(unknown)',
        branch: null,
        mode: 'unknown',
        startedAt: at ?? new Date().toISOString(),
        totalScenarios: null,
        finished: false,
        exitCode: null,
        counts: null,
        finishedAt: null,
        lastEventAt: at ?? new Date().toISOString(),
        scenarios: {},
      }
      runs.value[runId] = run
    }
    if (at) run.lastEventAt = at
    return run
  }

  function ensureScenario(run: RunState, uid: string): ScenarioState {
    let scenario = run.scenarios[uid]
    if (!scenario) {
      const slash = uid.indexOf('/')
      scenario = {
        uid,
        feature: slash > 0 ? uid.substring(0, slash) : '',
        scenario: slash > 0 ? uid.substring(slash + 1) : uid,
        status: 'running',
        attempt: 1,
        attempts: null,
        outcome: null,
        durationMs: null,
        errorMessage: null,
        retryReason: null,
        steps: [],
      }
      run.scenarios[uid] = scenario
    }
    return scenario
  }

  function handleRunStarted(e: RunStarted) {
    const run = ensureRun(e.runId, e.startedAt)
    run.suite = e.suite
    run.repository = e.repository
    run.branch = e.branch
    run.mode = e.mode
    run.startedAt = e.startedAt
    run.totalScenarios = e.totalScenarios
  }

  function handleRunHeartbeat(e: RunHeartbeat) {
    ensureRun(e.runId, e.at)
  }

  function handleRunFinished(e: RunFinished) {
    const run = ensureRun(e.runId, e.finishedAt)
    run.finished = true
    run.exitCode = e.exitCode
    run.finishedAt = e.finishedAt
    run.counts = {
      passed: e.passed,
      failed: e.failed,
      passedOnRetry: e.passedOnRetry,
      indeterminate: e.indeterminate,
    }
  }

  function handleScenarioStarted(e: ScenarioStarted) {
    const run = ensureRun(e.runId, e.at)
    const scenario = ensureScenario(run, e.uid)
    scenario.feature = e.feature
    scenario.scenario = e.scenario
    scenario.status = 'running'
    scenario.attempt = e.attempt
    // Every attempt gets the full reset/begin/end bracket, so the step list
    // starts over — the previous attempt's steps belong to the attempt history,
    // which the report views own, not the live view.
    scenario.steps = []
    scenario.errorMessage = null
  }

  function handleScenarioFinished(e: ScenarioFinished) {
    const run = ensureRun(e.runId)
    const scenario = ensureScenario(run, e.uid)
    scenario.attempts = e.attempts
    scenario.outcome = e.outcome
    scenario.durationMs = e.durationMs
    scenario.errorMessage = e.errorMessage
    scenario.status =
      e.outcome === 'CleanPass'
        ? 'passed'
        : e.outcome === 'PassOnRetry'
          ? 'passed-on-retry'
          : e.outcome === 'Aborted'
            ? 'aborted'
            : 'failed'
  }

  function handleRetryScheduled(e: RetryScheduled) {
    const run = ensureRun(e.runId)
    const scenario = ensureScenario(run, e.uid)
    scenario.status = 'retry-scheduled'
    scenario.retryReason = e.reason
  }

  function handleStepStarted(e: StepStarted) {
    const run = ensureRun(e.runId)
    const scenario = ensureScenario(run, e.uid)
    scenario.steps.push({
      stepId: e.stepId,
      kind: e.kind,
      text: e.text,
      status: 'running',
      durationMs: null,
      errorMessage: null,
    })
  }

  function handleStepFinished(e: StepFinished) {
    const run = ensureRun(e.runId)
    const scenario = ensureScenario(run, e.uid)
    const step = scenario.steps.find((s) => s.stepId === e.stepId)
    if (!step) return
    step.status = e.status === 'ok' || e.status === 'success' ? 'passed' : 'failed'
    step.durationMs = e.durationMs
    step.errorMessage = e.errorMessage
  }

  /** "Eject": drop a finished run from the dashboard. */
  function removeRun(runId: string) {
    delete runs.value[runId]
  }

  return {
    runs,
    allRuns,
    activeRuns,
    finishedRuns,
    runById,
    progressOf,
    handleRunStarted,
    handleRunHeartbeat,
    handleRunFinished,
    handleScenarioStarted,
    handleScenarioFinished,
    handleRetryScheduled,
    handleStepStarted,
    handleStepFinished,
    removeRun,
  }
})
