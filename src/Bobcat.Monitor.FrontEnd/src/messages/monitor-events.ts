/**
 * TypeScript mirrors of src/Bobcat.Monitor/Contracts/MonitorEvents.cs.
 *
 * Hand-written for now; the plan of record is to lift CritterWatch's NJsonSchema
 * codegen (records → .ts + relayToStore case insertion) once the contract count
 * justifies it. The envelope `type` strings are Wolverine's snake_case message
 * type names, which the C# side pins as JSON type discriminators too — one
 * spelling everywhere.
 */

export interface MonitorEnvelope {
  type: string
  data: unknown
}

/**
 * Server-side batching (SignalRBatchAccumulator): every event ingested within one flush tick
 * arrives as a single `batched_web_socket_payload` frame whose items are ordinary
 * {type, data} envelopes — relayToStore unwraps and re-dispatches each one.
 */
export interface BatchedWebSocketItem {
  type: string
  data: unknown
}

export interface BatchedWebSocketPayload {
  items: BatchedWebSocketItem[]
}

export interface RunStarted {
  runId: string
  suite: string
  repository: string
  branch: string | null
  mode: string
  startedAt: string
  totalScenarios: number | null
}

export interface RunHeartbeat {
  runId: string
  at: string
}

export interface RunFinished {
  runId: string
  exitCode: number
  passed: number
  failed: number
  passedOnRetry: number
  indeterminate: number
  finishedAt: string
}

export interface ScenarioStarted {
  runId: string
  uid: string
  feature: string
  scenario: string
  attempt: number
  at: string
  /** Steps this attempt will run; absent/null from a publisher that predates it. */
  totalSteps?: number | null
}

export type ScenarioOutcome = 'CleanPass' | 'PassOnRetry' | 'Failed' | 'Aborted'

export interface ScenarioFinished {
  runId: string
  uid: string
  outcome: ScenarioOutcome
  attempts: number
  durationMs: number
  errorMessage: string | null
}

export interface RetryScheduled {
  runId: string
  uid: string
  nextAttempt: number
  disposition: string
  reason: string
}

export interface StepStarted {
  runId: string
  uid: string
  stepId: string
  kind: string
  text: string
  /** 1-based position within the attempt; absent/null from an older publisher. */
  stepNumber?: number | null
  totalSteps?: number | null
  /** Milliseconds into the scenario's wall clock when the step started. */
  scenarioElapsedMs?: number | null
}

export interface StepFinished {
  runId: string
  uid: string
  stepId: string
  status: string
  durationMs: number
  errorMessage: string | null
  /** Milliseconds into the scenario's wall clock when the step finished. */
  scenarioElapsedMs?: number | null
}

/**
 * Interim progress from a step still running. Two shapes share it: a [TableGrammar] ticking
 * through rows (row/totalRows, no message) and a [WaitFor] poll loop reporting what it last
 * saw (message, no rows). elapsedMs is time since the step started. Latest wins per step.
 */
export interface StepProgress {
  runId: string
  uid: string
  stepId: string
  message: string | null
  row: number | null
  totalRows: number | null
  elapsedMs: number
}
