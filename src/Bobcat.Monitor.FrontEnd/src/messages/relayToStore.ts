import { useRunsStore } from '@/stores/runs-store'
import type {
  BatchedWebSocketPayload,
  MonitorEnvelope,
  RetryScheduled,
  RunFinished,
  RunHeartbeat,
  RunStarted,
  ScenarioFinished,
  ScenarioStarted,
  StepFinished,
  StepProgress,
  StepStarted,
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
    default:
      // Unknown message types are forward-compatibility, not errors.
      break
  }
}
