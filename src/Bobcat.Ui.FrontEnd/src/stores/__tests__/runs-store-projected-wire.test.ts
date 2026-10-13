import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { mount } from '@vue/test-utils'
import { relayToStore } from '@/messages/relayToStore'
import SpecificationView from '@/components/SpecificationView.vue'
import { splitDisplayName, stepStatusOf, useRunsStore, visibleScenarios, type RunState } from '../runs-store'
import { missingEventRun, supervisedRun } from './crittercrush-rehearsal.fixture'

/**
 * What Bobcat 0.60.1's projected lane really sends, replayed from runs captured off a live Stoat
 * during the 2026-10-12 CritterCrush rehearsal. Pinned against the server's fold of the same capture
 * in Stoat.Console.Tests/ProjectedLaneWireTests.
 */
function replay(lines: string[]): RunState {
  for (const line of lines) {
    // Exactly what hydration does with an archived line: wrap the flat event in its envelope.
    const event = JSON.parse(line) as { type: string; runId: string }
    relayToStore({ type: event.type, data: event })
  }
  const runId = (JSON.parse(lines[0]!) as { runId: string }).runId
  return useRunsStore().runById(runId)!
}

describe('the projected lane on the wire (2026-10-12 rehearsal)', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('reads every step of a passing projected spec as passed — the wire says "Passed", not "success"', () => {
    const run = replay(supervisedRun)
    const steps = visibleScenarios(run).flatMap((s) => s.steps)

    expect(steps).toHaveLength(11)
    expect(steps.every((s) => s.status === 'passed')).toBe(true)
  })

  it('draws no ✗ on the spec page of a passing run', () => {
    const run = replay(supervisedRun)
    const scenario = run.scenarios['AcceptHomeCheckAssignment/home check assignment accepted']!
    const wrapper = mount(SpecificationView, { props: { scenario } })

    const lines = wrapper.findAll('[data-testid="spec-line"]')
    expect(lines).toHaveLength(5)
    expect(lines.map((l) => l.attributes('data-status'))).toEqual(['passed', 'passed', 'passed', 'passed', 'passed'])
    expect(wrapper.text()).not.toContain('✗')
  })

  it('fails only the step that failed: a MISSING row reds the Then, never the Given and When', () => {
    const run = replay(missingEventRun)
    const scenario = run.scenarios['ApproveVolunteer/volunteer approved']!
    const wrapper = mount(SpecificationView, { props: { scenario } })

    const statuses = wrapper.findAll('[data-testid="spec-line"]').map((l) => l.attributes('data-status'))
    expect(statuses).toEqual(['passed', 'passed', 'failed'])
  })

  it('counts each test of a supervised projected run once, not twice', () => {
    // The rehearsal's run card: 19 tests read as 38 scenarios.
    const run = replay(supervisedRun)

    expect(Object.keys(run.scenarios)).toHaveLength(6) // both streams are folded…
    const visible = visibleScenarios(run)
    expect(visible).toHaveLength(3) // …and the forwarded copies are not shown or counted
    expect(visible.every((s) => s.uid.includes('/'))).toBe(true)
    expect(useRunsStore().progressOf(run)).toBeCloseTo(3 / 19)
  })

  it('counts once whichever stream arrives first', () => {
    const forwarded = supervisedRun.filter((l) => l.includes('"type": "test_') || l.includes('"type":"test_'))
    const rest = supervisedRun.filter((l) => !forwarded.includes(l))
    const run = replay([rest[0]!, ...forwarded, ...rest.slice(1)])

    expect(visibleScenarios(run)).toHaveLength(3)
  })

  it('keeps a forwarded test no worker scenario mirrors — a failing plain [Fact] is never hidden', () => {
    const run = replay(supervisedRun)
    useRunsStore().handleTestFinished({
      runId: run.runId,
      uid: '0f0f0f',
      displayName: 'CritterCrush.Specs.Plumbing.the_host_boots',
      state: 'Failed',
      durationMs: 12,
      lane: 0,
      at: '2026-10-12T19:22:36Z',
    })

    const visible = visibleScenarios(run)
    expect(visible).toHaveLength(4)
    expect(visible.find((s) => s.uid === '0f0f0f')!.status).toBe('failed')
  })

  it('tells two scenarios with one title apart by the class, and never guesses', () => {
    setActivePinia(createPinia())
    const store = useRunsStore()
    const runId = 'b0b0b0b0-0000-0000-0000-000000000001'
    for (const uid of ['Orders/happy path', 'Refunds/happy path']) {
      store.handleScenarioFinished({ runId, uid, outcome: 'CleanPass', attempts: 1, durationMs: 5, errorMessage: null })
    }
    const at = '2026-10-12T19:00:00Z'
    store.handleTestFinished({ runId, uid: 'a1', displayName: 'Specs.Orders.happy_path', state: 'Passed', durationMs: 5, lane: 0, at })
    store.handleTestFinished({ runId, uid: 'b2', displayName: 'Specs.Misc.happy_path', state: 'Passed', durationMs: 5, lane: 0, at })

    expect(visibleScenarios(store.runById(runId)!).map((s) => s.uid).sort()).toEqual([
      'Orders/happy path',
      'Refunds/happy path',
      'b2',
    ])
  })
})

describe('stepStatusOf', () => {
  it.each([
    ['Passed', 'passed'],
    ['passed', 'passed'],
    ['success', 'passed'],
    ['ok', 'passed'],
    ['Failed', 'failed'],
    ['error', 'failed'],
    ['missing', 'failed'],
  ])('reads %s as %s', (wire, verdict) => {
    expect(stepStatusOf(wire)).toBe(verdict)
  })
})

describe('splitDisplayName', () => {
  it('cuts a theory argument list before splitting on dots', () => {
    expect(splitDisplayName('Specs.Calc.adds_two(a: 1.5, b: 2)')).toEqual({ className: 'Calc', method: 'adds_two' })
    expect(splitDisplayName('adds_two')).toEqual({ className: '', method: 'adds_two' })
  })
})
