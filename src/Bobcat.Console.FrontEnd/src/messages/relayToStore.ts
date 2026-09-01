import { useEventModelStore } from '@/stores/event-model-store'
import { useRunsStore } from '@/stores/runs-store'
import type {
  BatchedWebSocketPayload,
  LaneFinished,
  LaneStarted,
  MonitorEnvelope,
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
  TestFinished,
  TestStalled,
  TestStarted,
  WorkerFaulted,
  WorkerStarted,
} from './monitor-events'

/**
 * Fan incoming SignalR envelopes out to the Pinia stores — the CritterWatch
 * dispatcher pattern: one switch on the snake_case message type name, stores
 * never touch SignalR themselves.
 */
export function relayToStore(message: unknown): void {
  const envelope = (typeof message === 'string' ? JSON.parse(message) : message) as MonitorEnvelope
  if (!envelope || typeof envelope.type !== 'string') return

  const runs = useRunsStore()

  switch (envelope.type) {
    case 'batched_web_socket_payload': {
      // Server-side batched frame: unwrap and dispatch each inner {type, data} item in
      // order — every item is exactly the per-event envelope the cases below handle.
      const batch = envelope.data as BatchedWebSocketPayload
      for (const item of batch?.items ?? []) {
        relayToStore(item)
      }
      break
    }
    case 'run_started':
      runs.handleRunStarted(envelope.data as RunStarted)
      break
    case 'run_heartbeat':
      runs.handleRunHeartbeat(envelope.data as RunHeartbeat)
      break
    case 'run_finished':
      runs.handleRunFinished(envelope.data as RunFinished)
      break
    case 'scenario_started':
      runs.handleScenarioStarted(envelope.data as ScenarioStarted)
      break
    case 'scenario_finished':
      runs.handleScenarioFinished(envelope.data as ScenarioFinished)
      break
    case 'retry_scheduled':
      runs.handleRetryScheduled(envelope.data as RetryScheduled)
      break
    case 'step_started':
      runs.handleStepStarted(envelope.data as StepStarted)
      break
    case 'step_finished':
      runs.handleStepFinished(envelope.data as StepFinished)
      break
    case 'step_progress':
      runs.handleStepProgress(envelope.data as StepProgress)
      break
    case 'lane_started':
      runs.handleLaneStarted(envelope.data as LaneStarted)
      break
    case 'lane_finished':
      runs.handleLaneFinished(envelope.data as LaneFinished)
      break
    case 'resource_recycled':
      runs.handleResourceRecycled(envelope.data as ResourceRecycled)
      break
    case 'worker_faulted':
      runs.handleWorkerFaulted(envelope.data as WorkerFaulted)
      break
    case 'worker_started':
      runs.handleWorkerStarted(envelope.data as WorkerStarted)
      break
    case 'test_stalled':
      runs.handleTestStalled(envelope.data as TestStalled)
      break
    case 'run_progress':
      runs.handleRunProgress(envelope.data as RunProgress)
      break
    case 'test_started':
      runs.handleTestStarted(envelope.data as TestStarted)
      break
    case 'test_finished':
      runs.handleTestFinished(envelope.data as TestFinished)
      break
    // *CASE ABOVE* -- generated cases are inserted above this line; keep it.
    // Issue #169 — hand-written, and deliberately BELOW the marker: EventModelChanged is not a
    // MonitorEvent, so the generator that fills the block above will never emit it and would
    // otherwise be entitled to clobber anything it found in there.
    //
    // The push carries the model's NAME, not the document — a whole-document replace can be large,
    // and the page already has a GET that serves it. Re-fetching keeps one definition of how a
    // model is loaded instead of two that can disagree.
    case 'event_model_changed':
      void useEventModelStore().refresh()
      break
    default:
      // Unknown message types are forward-compatibility, not errors.
      break
  }
}
