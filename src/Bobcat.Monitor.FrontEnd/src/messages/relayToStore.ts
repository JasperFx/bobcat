import { useRunsStore } from '@/stores/runs-store'
import type {
  MonitorEnvelope,
  RetryScheduled,
  RunFinished,
  RunHeartbeat,
  RunStarted,
  ScenarioFinished,
  ScenarioStarted,
  StepFinished,
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
    default:
      // Unknown message types are forward-compatibility, not errors.
      break
  }
}
