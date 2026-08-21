/**
 * GENERATED FILE — do not edit by hand.
 *
 * TypeScript mirrors of src/Bobcat.Monitor/Contracts/MonitorEvents.cs, emitted by
 * TypeScriptContracts.cs (NJsonSchema over the C# records, through the serializer settings
 * the wire uses). Regenerate with:
 *
 *   dotnet run --project src/Bobcat.Monitor -- generate
 *
 * TypeScriptContractTests fails the build when this file drifts from the C# source, so a
 * new or changed record shows up here by regenerating, never by hand-editing. The envelope
 * `type` strings are Wolverine's snake_case message type names, which the C# side pins as
 * the JSON type discriminators too — one spelling everywhere.
 */

/** The {type, data} frame relayToStore switches on — the transport's shape, not a contract record. */
export interface MonitorEnvelope {
  type: string
  data: unknown
}

/** Every envelope `type` the monitor relays — the [JsonDerivedType] discriminators on MonitorEvent. */
export type MonitorEventType =
  | 'run_started'
  | 'run_heartbeat'
  | 'run_finished'
  | 'scenario_started'
  | 'scenario_finished'
  | 'retry_scheduled'
  | 'step_started'
  | 'step_finished'
  | 'lane_started'
  | 'lane_finished'
  | 'resource_recycled'
  | 'worker_faulted'

export interface MonitorEvent {
  runId: string
  /** The STJ discriminator — already dispatched on by relayToStore, so handlers never need it. */
  type?: MonitorEventType
}

/** Envelope type: 'run_started' */
export interface RunStarted extends MonitorEvent {
  suite: string
  repository: string
  branch: string | null
  mode: string
  startedAt: string
  totalScenarios: number | null
  tag?: string | null
}

/** Envelope type: 'run_heartbeat' */
export interface RunHeartbeat extends MonitorEvent {
  at: string
}

/** Envelope type: 'run_finished' */
export interface RunFinished extends MonitorEvent {
  exitCode: number
  passed: number
  failed: number
  passedOnRetry: number
  indeterminate: number
  finishedAt: string
}

/** Envelope type: 'scenario_started' */
export interface ScenarioStarted extends MonitorEvent {
  uid: string
  feature: string
  scenario: string
  attempt: number
  at: string
}

/** Envelope type: 'scenario_finished' */
export interface ScenarioFinished extends MonitorEvent {
  uid: string
  outcome: string
  attempts: number
  durationMs: number
  errorMessage: string | null
}

/** Envelope type: 'retry_scheduled' */
export interface RetryScheduled extends MonitorEvent {
  uid: string
  nextAttempt: number
  disposition: string
  reason: string
}

/** Envelope type: 'step_started' */
export interface StepStarted extends MonitorEvent {
  uid: string
  stepId: string
  kind: string
  text: string
}

/** Envelope type: 'step_finished' */
export interface StepFinished extends MonitorEvent {
  uid: string
  stepId: string
  status: string
  durationMs: number
  errorMessage: string | null
}

/** Envelope type: 'lane_started' */
export interface LaneStarted extends MonitorEvent {
  lane: number
  uids: string[]
  at: string
}

/** Envelope type: 'lane_finished' */
export interface LaneFinished extends MonitorEvent {
  lane: number
  outcomes: number
  crashed: boolean
  at: string
}

/** Envelope type: 'resource_recycled' */
export interface ResourceRecycled extends MonitorEvent {
  resource: string
  at: string
}

/** Envelope type: 'worker_faulted' */
export interface WorkerFaulted extends MonitorEvent {
  lane: number | null
  fault: string
  exitCode: number | null
  standardError: string | null
  at: string
}

export interface BatchedWebSocketPayload {
  items: BatchedWebSocketItem[]
}

export interface BatchedWebSocketItem {
  type: string
  data: MonitorEvent
}
