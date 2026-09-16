import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useRunsStore } from '../runs-store'

/**
 * Issue #322 — a step repeated within one scenario is its own row.
 *
 * `stepId` is a step TEMPLATE id (the step method's name), not an identity, and the two step
 * shapes the grammar most recently encouraged both repeat: `Given {event} occurred` once per
 * arranged event (#259) and `And no events for {aggregate} "…"` once per re-pointed stream
 * (#311, #320). Keying on stepId collapsed every repeat into one row carrying the LAST
 * occurrence's text and the FIRST one's duration — not a view of anything — and every occurrence
 * after the first never reached a terminal status.
 */
const RUN = '9a1f1a1e-0000-0000-0000-000000000322'
const UID = 'MyAppointments/An owner’s page spans every appointment stream they have'

function store() {
  const s = useRunsStore()
  s.handleRunStarted({
    runId: RUN,
    suite: 'CritterCrush.Specs',
    repository: '/repo',
    branch: 'main',
    mode: 'mtp-host',
    startedAt: '2026-09-16T10:00:00Z',
    totalScenarios: 1,
  })
  s.handleScenarioStarted({
    runId: RUN,
    uid: UID,
    feature: 'MyAppointments',
    scenario: 'An owner’s page spans every appointment stream they have',
    attempt: 1,
    at: '2026-09-16T10:00:01Z',
    totalSteps: 6,
  })
  return s
}

/** The fan-out scenario's real step sequence: two stream re-points, three arranged events. */
const SEQUENCE: Array<{ stepId: string; kind: string; text: string }> = [
  { stepId: 'GivenNoEventsFor', kind: 'Given', text: 'no events for Appointment "2542…"' },
  { stepId: 'GivenEventOccurred', kind: 'Given', text: 'HomeCheckAppointmentProposed occurred' },
  { stepId: 'GivenEventOccurred', kind: 'Given', text: 'AppointmentConfirmed occurred' },
  { stepId: 'GivenNoEventsFor', kind: 'Given', text: 'no events for Appointment "6221…"' },
  { stepId: 'GivenEventOccurred', kind: 'Given', text: 'SurrenderIntakeAppointmentProposed occurred' },
  { stepId: 'ThenReadModelWithIdContains', kind: 'Then', text: 'the MyAppointments read model … contains' },
]

function runAll(s: ReturnType<typeof useRunsStore>, finish = true) {
  SEQUENCE.forEach((step, i) => {
    s.handleStepStarted({ runId: RUN, uid: UID, ...step, stepNumber: i + 1, totalSteps: 6 })
    if (finish) {
      s.handleStepFinished({
        runId: RUN,
        uid: UID,
        stepId: step.stepId,
        status: 'success',
        durationMs: (i + 1) * 10,
        errorMessage: null,
      })
    }
  })
  return s.runs[RUN].scenarios[UID]
}

describe('runs-store repeated steps', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('keeps every occurrence as its own row, in order', () => {
    const scenario = runAll(store())

    expect(scenario.steps.length).toBe(6)
    expect(scenario.steps.map((s) => s.text)).toEqual(SEQUENCE.map((s) => s.text))
  })

  it('finishes every occurrence, not just the first of each stepId', () => {
    const scenario = runAll(store())

    // The headline of #322: in a passing run, 33 of 182 steps sat at "running" forever.
    expect(scenario.steps.filter((s) => s.status === 'running')).toEqual([])
    expect(scenario.steps.map((s) => s.durationMs)).toEqual([10, 20, 30, 40, 50, 60])
  })

  it('pairs each finish with the occurrence that was still running', () => {
    const scenario = runAll(store())

    // Not the last-wins or first-wins collapse: the second GivenNoEventsFor must carry ITS own
    // duration (40), not the first one's (10).
    const repoints = scenario.steps.filter((s) => s.stepId === 'GivenNoEventsFor')
    expect(repoints.map((s) => s.durationMs)).toEqual([10, 40])
    expect(repoints.map((s) => s.text)).toEqual([SEQUENCE[0].text, SEQUENCE[3].text])
  })

  it('is idempotent under hydration replaying the same stream', () => {
    const s = store()
    runAll(s)
    const again = runAll(s)

    // Hydration replays the archive over live state. Keying on stepNumber keeps that an upsert
    // rather than a duplicate — the reason the old code keyed on stepId at all.
    expect(again.steps.length).toBe(6)
    expect(again.steps.map((s) => s.text)).toEqual(SEQUENCE.map((x) => x.text))
  })

  it('still upserts by stepId when a publisher sends no step numbers', () => {
    const s = store()
    s.handleStepStarted({ runId: RUN, uid: UID, stepId: 'OnlyStep', kind: 'Given', text: 'first' })
    s.handleStepStarted({ runId: RUN, uid: UID, stepId: 'OnlyStep', kind: 'Given', text: 'first' })

    expect(s.runs[RUN].scenarios[UID].steps.length).toBe(1)
  })
})
