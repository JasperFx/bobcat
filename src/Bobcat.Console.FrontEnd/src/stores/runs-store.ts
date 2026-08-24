import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import type {
  LaneFinished,
  LaneStarted,
  ResourceRecycled,
  RetryScheduled,
  RunFinished,
  RunHeartbeat,
  RunProgress,
  RunStarted,
  ScenarioFinished,
  ScenarioStarted,
  StepFinished,
  StepProgress,
  StepStarted,
  TestStalled,
  WorkerFaulted,
  WorkerStarted,
} from '@/messages/monitor-events'

export type StepStatus = 'running' | 'passed' | 'failed'

/**
 * The latest interim progress a running step reported — a [TableGrammar] row tick
 * (row/totalRows) or a [WaitFor] poll message. Latest wins; cleared when the step finishes.
 */
export interface StepProgressState {
  message: string | null
  row: number | null
  totalRows: number | null
  /** Milliseconds since the step started, as of this update. */
  elapsedMs: number
}

export interface StepState {
  stepId: string
  kind: string
  text: string
  status: StepStatus
  durationMs: number | null
  errorMessage: string | null
  /** 1-based position within the attempt; null from a publisher that predates it. */
  stepNumber: number | null
  /** Milliseconds into the scenario's wall clock when the step started; null if unknown. */
  scenarioElapsedMs: number | null
  progress: StepProgressState | null
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
  /**
   * The attempt number a retry_scheduled promised, waiting for its start event. Only the
   * supervisor knows a run is a retry — its worker is a fresh process (or at best a fresh
   * runner in a reused one) and starts counting at one, so without this the third try
   * showed as attempt 1.
   */
  scheduledAttempt: number | null
  outcome: string | null
  durationMs: number | null
  errorMessage: string | null
  retryReason: string | null
  /** Steps of the CURRENT attempt only — a retry starts a fresh bracket. */
  steps: StepState[]
  /**
   * How many steps the current attempt will run, from scenario_started (falling back to the
   * first step_started that names it). Null until a publisher says — older publishers never do.
   */
  totalSteps: number | null
}

// The supervisor's topology (issue #84): which worker process is doing what, what
// infrastructure was thrown away, and which workers died. Only a supervised run has any of
// it; an in-process run's arrays stay empty and the views show nothing.

export type LaneStatus = 'running' | 'finished' | 'crashed'

export interface LaneState {
  lane: number
  status: LaneStatus
  /**
   * The uids handed to the lane's worker on its latest start. A same-process retry goes back
   * to the lane the test ran in, carrying only the tests being retried — so this is "what the
   * lane is working through now", not everything it ever ran.
   */
  uids: string[]
  /** How many times the lane has been handed work: 1 is the first pass, more are retry passes. */
  passes: number
  startedAt: string
  finishedAt: string | null
  /** Outcomes the worker reported on its latest finish; null while it is still running. */
  outcomes: number | null
  /**
   * The OS pid of the lane's worker process (issue #146), from worker_started — the handle an
   * external diagnostic must be pointed at. Null until the pid is announced.
   */
  processId: number | null
}

export interface RecycleState {
  resource: string
  at: string
}

/**
 * A test the supervisor reported stalled (issue #145): the name a hung run's log otherwise
 * cannot produce, how long it had been in flight at detection, and where it was running.
 */
export interface StallState {
  uid: string
  displayName: string
  inFlightMs: number
  /** The lane it was running in; null for a one-test isolated or recycled process. */
  lane: number | null
  at: string
}

/**
 * The supervisor's latest progress heartbeat (issue #148) — the only live progress a
 * foreign-framework worker (xUnit, tUnit) gives, since it streams no scenario events. The
 * longest-running trio is null when nothing is in flight; peakWorkerRssBytes is null unless
 * memory sampling (issue #149) is on — unmeasured is never zero.
 */
export interface RunProgressState {
  elapsedMs: number
  completed: number
  total: number
  inFlight: number
  longestRunningUid: string | null
  longestRunningDisplayName: string | null
  longestRunningMs: number | null
  peakWorkerRssBytes: number | null
  at: string
}

export interface WorkerFaultState {
  /** The lane whose worker died; null for a one-test isolated or recycled process. */
  lane: number | null
  /** The sentence the supervisor's report collects — dashboard and report agree. */
  fault: string
  exitCode: number | null
  standardError: string | null
  at: string
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
  /** Server-declared: rehydrated from an archive with no terminal event — will never finish. */
  orphaned: boolean
  exitCode: number | null
  counts: RunCounts | null
  finishedAt: string | null
  /** Wall-clock of the latest event/heartbeat — the orphaned-run detector's input. */
  lastEventAt: string
  scenarios: Record<string, ScenarioState>
  /** Supervisor lanes in lane order; empty for an in-process run. */
  lanes: LaneState[]
  /** Resources the supervisor threw away and stood up again, in order. */
  recycles: RecycleState[]
  /** Worker processes that died, in order, with the account of each. */
  faults: WorkerFaultState[]
  /** Tests the supervisor reported as stalled (issue #145), in detection order. */
  stalls: StallState[]
  /** The supervisor's latest progress heartbeat (issue #148); null until one arrives. */
  progress: RunProgressState | null
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
        orphaned: false,
        exitCode: null,
        counts: null,
        finishedAt: null,
        lastEventAt: at ?? new Date().toISOString(),
        scenarios: {},
        lanes: [],
        recycles: [],
        faults: [],
        stalls: [],
        progress: null,
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
        scheduledAttempt: null,
        outcome: null,
        durationMs: null,
        errorMessage: null,
        retryReason: null,
        steps: [],
        totalSteps: null,
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
    // A supervised retry's worker counts from one, so a scheduled attempt number wins over the
    // one on the wire. Taken as a floor rather than an assignment: a re-announced start
    // (hydration replaying the archive over live state) must not un-know an attempt we already
    // watched happen. Same fold as the server-side RunProjection.
    scenario.attempt = Math.max(e.attempt, scenario.scheduledAttempt ?? 0, scenario.attempt)
    scenario.scheduledAttempt = null
    // Every attempt gets the full reset/begin/end bracket, so the step list
    // starts over — the previous attempt's steps belong to the attempt history,
    // which the report views own, not the live view.
    scenario.steps = []
    scenario.errorMessage = null
    scenario.totalSteps = e.totalSteps ?? null
  }

  function handleScenarioFinished(e: ScenarioFinished) {
    const run = ensureRun(e.runId)
    const scenario = ensureScenario(run, e.uid)
    // A worker reporting "1 attempt" is reporting its own count; a total can never be fewer
    // than the attempts we watched start.
    scenario.attempts = Math.max(e.attempts, scenario.attempt)
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
    scenario.scheduledAttempt = e.nextAttempt
  }

  function handleStepStarted(e: StepStarted) {
    const run = ensureRun(e.runId)
    const scenario = ensureScenario(run, e.uid)
    const step: StepState = {
      stepId: e.stepId,
      kind: e.kind,
      text: e.text,
      status: 'running',
      durationMs: null,
      errorMessage: null,
      stepNumber: e.stepNumber ?? null,
      scenarioElapsedMs: e.scenarioElapsedMs ?? null,
      progress: null,
    }
    // A publisher that announced the count on the step rather than the scenario (or whose
    // scenario_started was dropped on backpressure) still tells us how many there are.
    if (e.totalSteps != null && scenario.totalSteps == null) scenario.totalSteps = e.totalSteps
    // Upsert by stepId rather than blind push: hydration replays the archived stream over
    // whatever live events already arrived, and a duplicated step_started must not render
    // the step twice.
    const existing = scenario.steps.findIndex((s) => s.stepId === e.stepId)
    if (existing >= 0) scenario.steps[existing] = step
    else scenario.steps.push(step)
  }

  function handleStepFinished(e: StepFinished) {
    const run = ensureRun(e.runId)
    const scenario = ensureScenario(run, e.uid)
    const step = scenario.steps.find((s) => s.stepId === e.stepId)
    if (!step) return
    step.status = e.status === 'ok' || e.status === 'success' ? 'passed' : 'failed'
    step.durationMs = e.durationMs
    step.errorMessage = e.errorMessage
    // Interim progress describes a step in flight; once it has a verdict the "row 140 of
    // 200" or "waiting… attempt 7" would be stale.
    step.progress = null
  }

  /**
   * Interim progress for a running step — latest wins. A step_progress that outruns its
   * step_started (or whose step_started was dropped) is not an error: the step is
   * synthesized as running so the progress still lands somewhere visible.
   */
  function handleStepProgress(e: StepProgress) {
    const run = ensureRun(e.runId)
    const scenario = ensureScenario(run, e.uid)
    let step = scenario.steps.find((s) => s.stepId === e.stepId)
    if (!step) {
      step = {
        stepId: e.stepId,
        kind: '',
        text: '',
        status: 'running',
        durationMs: null,
        errorMessage: null,
        stepNumber: null,
        scenarioElapsedMs: null,
        progress: null,
      }
      scenario.steps.push(step)
    }
    // A finished step ignores late progress — hydration can replay a step_progress after the
    // step_finished that already superseded it.
    if (step.status !== 'running') return
    step.progress = {
      message: e.message,
      row: e.row,
      totalRows: e.totalRows,
      elapsedMs: e.elapsedMs,
    }
  }

  /**
   * The scenarios a lane's worker is running right now — the uids of its latest pass joined
   * to their live scenario state. A foreign-framework worker (xUnit, tUnit) streams no
   * scenarios, so for it this is always empty and the lane itself is the whole signal.
   */
  function runningIn(run: RunState, lane: LaneState): ScenarioState[] {
    return lane.uids
      .map((uid) => run.scenarios[uid])
      .filter((s): s is ScenarioState => s !== undefined && s.status === 'running')
  }

  /** Whether the run has any supervisor topology worth rendering. */
  function hasTopology(run: RunState): boolean {
    return (
      run.lanes.length > 0 || run.recycles.length > 0 || run.faults.length > 0 || run.stalls.length > 0
    )
  }

  function ensureLane(run: RunState, index: number, at: string): LaneState {
    let lane = run.lanes.find((l) => l.lane === index)
    if (!lane) {
      lane = {
        lane: index,
        status: 'running',
        uids: [],
        passes: 0,
        startedAt: at,
        finishedAt: null,
        outcomes: null,
        processId: null,
      }
      run.lanes.push(lane)
      run.lanes.sort((a, b) => a.lane - b.lane)
    }
    return lane
  }

  function handleLaneStarted(e: LaneStarted) {
    const run = ensureRun(e.runId, e.at)
    const lane = ensureLane(run, e.lane, e.at)
    // The supervisor's own clock orders a lane's passes. A start no newer than the one we are
    // already on is a replay — hydration re-announces the archive over live state — and must
    // not count as another pass or reset the lane. Equal means the very same start.
    if (lane.passes > 0 && Date.parse(e.at) <= Date.parse(lane.startedAt)) {
      if (e.at === lane.startedAt) lane.uids = [...e.uids]
      return
    }
    // A new pass: the first, or a same-process retry handed back to the lane the test ran in.
    lane.status = 'running'
    lane.uids = [...e.uids]
    lane.passes += 1
    lane.startedAt = e.at
    lane.finishedAt = null
    lane.outcomes = null
  }

  function handleLaneFinished(e: LaneFinished) {
    const run = ensureRun(e.runId, e.at)
    const lane = ensureLane(run, e.lane, e.at)
    if (lane.passes === 0) lane.passes = 1 // a finish whose start was dropped or never seen
    // A finish older than the pass we are on belongs to an earlier pass — replayed history.
    if (Date.parse(e.at) < Date.parse(lane.startedAt)) return
    lane.status = e.crashed ? 'crashed' : 'finished'
    lane.finishedAt = e.at
    lane.outcomes = e.outcomes
  }

  function handleResourceRecycled(e: ResourceRecycled) {
    const run = ensureRun(e.runId, e.at)
    // Replay guard: the same recycle never lands twice.
    if (run.recycles.some((r) => r.resource === e.resource && r.at === e.at)) return
    run.recycles.push({ resource: e.resource, at: e.at })
  }

  function handleWorkerFaulted(e: WorkerFaulted) {
    const run = ensureRun(e.runId, e.at)
    if (run.faults.some((f) => f.at === e.at && f.lane === e.lane && f.fault === e.fault)) return
    run.faults.push({
      lane: e.lane,
      fault: e.fault,
      exitCode: e.exitCode,
      standardError: e.standardError,
      at: e.at,
    })
  }

  function handleWorkerStarted(e: WorkerStarted) {
    const run = ensureRun(e.runId, e.at)
    // The pid folds onto the lane it belongs to — lane-to-pid correlation (issue #146). A
    // one-test process has no lane slot here; its pid still travels on any test_stalled or
    // worker_faulted it produces. A replacement worker's own start updates the pid.
    if (e.lane !== null && e.processId !== null) {
      ensureLane(run, e.lane, e.at).processId = e.processId
    }
  }

  function handleTestStalled(e: TestStalled) {
    const run = ensureRun(e.runId, e.at)
    // Replay guard: hydration re-announces the archive over live state.
    if (run.stalls.some((s) => s.uid === e.uid && s.at === e.at)) return
    run.stalls.push({
      uid: e.uid,
      displayName: e.displayName,
      inFlightMs: e.inFlightMs,
      lane: e.lane,
      at: e.at,
    })
  }

  function handleRunProgress(e: RunProgress) {
    const run = ensureRun(e.runId, e.at)
    // Latest wins, and a replayed older heartbeat never rolls progress backwards — the
    // supervisor's elapsed clock orders them without trusting arrival order.
    if (run.progress && e.elapsedMs < run.progress.elapsedMs) return
    run.progress = {
      elapsedMs: e.elapsedMs,
      completed: e.completed,
      total: e.total,
      inFlight: e.inFlight,
      longestRunningUid: e.longestRunningUid,
      longestRunningDisplayName: e.longestRunningDisplayName,
      longestRunningMs: e.longestRunningMs,
      peakWorkerRssBytes: e.peakWorkerRssBytes,
      at: e.at,
    }
  }

  /** "Eject": drop a finished run from the dashboard. */
  function removeRun(runId: string) {
    delete runs.value[runId]
  }

  /** Server-declared orphan (rehydrated, publisher gone). Cleared by any later run event. */
  function markOrphaned(runId: string) {
    const run = runs.value[runId]
    if (run && !run.finished) run.orphaned = true
  }

  /**
   * Reconcile after hydration: drop local runs the server no longer knows (ejected from
   * another browser, or a registry restart that skipped them).
   */
  function pruneTo(runIds: string[]) {
    const keep = new Set(runIds)
    for (const runId of Object.keys(runs.value)) {
      if (!keep.has(runId)) delete runs.value[runId]
    }
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
    handleStepProgress,
    handleLaneStarted,
    handleLaneFinished,
    handleResourceRecycled,
    handleWorkerFaulted,
    handleWorkerStarted,
    handleTestStalled,
    handleRunProgress,
    runningIn,
    hasTopology,
    removeRun,
    markOrphaned,
    pruneTo,
  }
})
