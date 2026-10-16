/**
 * GENERATED FILE — do not edit by hand.
 *
 * TypeScript mirrors of src/Bobcat.Ui/Contracts/MonitorEvents.cs, emitted by
 * TypeScriptContracts.cs (NJsonSchema over the C# records, through the serializer settings
 * the wire uses). Regenerate with:
 *
 *   dotnet run --project src/Bobcat.Console -- generate-ui-contracts
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
  | 'step_progress'
  | 'lane_started'
  | 'lane_finished'
  | 'resource_recycled'
  | 'worker_faulted'
  | 'worker_started'
  | 'test_stalled'
  | 'test_started'
  | 'test_finished'
  | 'run_progress'

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
  session?: string | null
  command?: string | null
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
  totalSteps?: number | null
  declaredSteps?: DeclaredStepInfo[] | null
  plannedSteps?: PlannedStepInfo[] | null
  parentSpecId?: string | null
}

export interface DeclaredStepInfo {
  keyword: string
  text: string
}

export interface PlannedStepInfo {
  keyword: string
  template: string
  grammar: string
  declaredStepNumber: number | null
}

/** Envelope type: 'scenario_finished' */
export interface ScenarioFinished extends MonitorEvent {
  uid: string
  outcome: string
  attempts: number
  durationMs: number
  errorMessage: string | null
  touchedTypes?: TouchedType[] | null
  at?: string | null
  reports?: ScenarioReportInfo[] | null
  stepsMs?: number | null
  parentSpecId?: string | null
}

export interface TouchedType {
  name: string
  fullName: string
  assemblyName: string
}

export interface ScenarioReportInfo {
  title: string
  columns: string[]
  cells: StepCell[]
  shortTitle: string | null
  suppressedRows: number
}

export interface StepCell {
  name: string
  status: string
  expected: string | null
  actual: string | null
  note: string | null
  rowIndex: number
  value: string | null
  comparison: string | null
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
  stepNumber?: number | null
  totalSteps?: number | null
  scenarioElapsedMs?: number | null
  declaredStepNumber?: number | null
  plannedStepNumber?: number | null
  values?: StepValueSpan[] | null
}

export interface StepValueSpan {
  start: number
  length: number
}

/** Envelope type: 'step_finished' */
export interface StepFinished extends MonitorEvent {
  uid: string
  stepId: string
  status: string
  durationMs: number
  errorMessage: string | null
  scenarioElapsedMs?: number | null
  cells?: StepCell[] | null
  columns?: string[] | null
  logs?: string[] | null
  diagnostics?: { [key: string]: string; } | null
  exceptionType?: string | null
  stackTrace?: string | null
  stackFrames?: string[] | null
  hiddenStackFrames?: number
}

/** Envelope type: 'step_progress' */
export interface StepProgress extends MonitorEvent {
  uid: string
  stepId: string
  message: string | null
  row: number | null
  totalRows: number | null
  elapsedMs: number
  cells?: StepCell[] | null
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

/** Envelope type: 'worker_started' */
export interface WorkerStarted extends MonitorEvent {
  lane: number | null
  purpose: string
  processId: number | null
  at: string
}

/** Envelope type: 'test_stalled' */
export interface TestStalled extends MonitorEvent {
  uid: string
  displayName: string
  inFlightMs: number
  lane: number | null
  processId: number | null
  at: string
}

/** Envelope type: 'test_started' */
export interface TestStarted extends MonitorEvent {
  uid: string
  displayName: string
  lane: number | null
  at: string
  specId?: string | null
}

/** Envelope type: 'test_finished' */
export interface TestFinished extends MonitorEvent {
  uid: string
  displayName: string
  state: string
  durationMs: number | null
  lane: number | null
  at: string
  specId?: string | null
}

/** Envelope type: 'run_progress' */
export interface RunProgress extends MonitorEvent {
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

export interface BatchedWebSocketPayload {
  items: BatchedWebSocketItem[]
}

export interface BatchedWebSocketItem {
  type: string
  data: MonitorEvent
}
