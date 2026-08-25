import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import ScenarioProgress from '../ScenarioProgress.vue'
import type { ScenarioState, StepState } from '@/stores/runs-store'

/**
 * The per-scenario progress indicator: step n of N, row k of M, waiting-for with elapsed.
 * Driven straight from store state shaped the way the fold leaves it.
 */
function step(overrides: Partial<StepState> & { stepId: string }): StepState {
  return {
    kind: 'Given',
    text: 'a thing',
    status: 'running',
    durationMs: null,
    errorMessage: null,
    stepNumber: null,
    scenarioElapsedMs: null,
    progress: null,
    ...overrides,
  }
}

function scenario(overrides: Partial<ScenarioState> = {}): ScenarioState {
  return {
    uid: 'Customers/Bulk import',
    feature: 'Customers',
    scenario: 'Bulk import',
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
    touchedTypes: [],
    finishedAt: null,
    ...overrides,
  }
}

describe('ScenarioProgress', () => {
  it('renders nothing for a scenario with a verdict', () => {
    const wrapper = mount(ScenarioProgress, {
      props: { scenario: scenario({ status: 'passed', steps: [step({ stepId: 's1', status: 'passed' })] }) },
    })
    expect(wrapper.find('[data-testid="scenario-progress"]').exists()).toBe(false)
  })

  it('renders nothing while running but before the first step', () => {
    const wrapper = mount(ScenarioProgress, { props: { scenario: scenario({ totalSteps: 3 }) } })
    expect(wrapper.find('[data-testid="scenario-progress"]').exists()).toBe(false)
  })

  it('shows step n of N with the current step text and a bar', () => {
    const wrapper = mount(ScenarioProgress, {
      props: {
        scenario: scenario({
          totalSteps: 9,
          steps: [
            step({ stepId: 's1', status: 'passed', stepNumber: 1 }),
            step({ stepId: 's2', status: 'passed', stepNumber: 2 }),
            step({ stepId: 's3', kind: 'When', text: 'it ships', stepNumber: 3 }),
          ],
        }),
      },
    })

    expect(wrapper.find('[data-testid="step-label"]').text()).toBe('step 3 of 9')
    expect(wrapper.find('[data-testid="scenario-progress"]').text()).toContain('it ships')
    expect(wrapper.find('[data-testid="step-bar"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="row-label"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="waiting-label"]').exists()).toBe(false)
  })

  it('counts the step by position when the publisher did not number it, and omits the bar without a total', () => {
    const wrapper = mount(ScenarioProgress, {
      props: {
        scenario: scenario({
          steps: [step({ stepId: 's1', status: 'passed' }), step({ stepId: 's2' })],
        }),
      },
    })

    expect(wrapper.find('[data-testid="step-label"]').text()).toBe('step 2')
    expect(wrapper.find('[data-testid="step-bar"]').exists()).toBe(false)
  })

  it('shows row k of M for a table grammar in flight', () => {
    const wrapper = mount(ScenarioProgress, {
      props: {
        scenario: scenario({
          totalSteps: 1,
          steps: [
            step({
              stepId: 'grammar',
              text: 'the following customers exist',
              stepNumber: 1,
              progress: { message: null, row: 140, totalRows: 200, elapsedMs: 3100 },
            }),
          ],
        }),
      },
    })

    expect(wrapper.find('[data-testid="row-label"]').text()).toContain('row 140 of 200')
    expect(wrapper.find('[data-testid="row-bar"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="waiting-label"]').exists()).toBe(false)
  })

  it('shows the wait-for message with elapsed', () => {
    const wrapper = mount(ScenarioProgress, {
      props: {
        scenario: scenario({
          totalSteps: 2,
          steps: [
            step({ stepId: 's1', status: 'passed', stepNumber: 1 }),
            step({
              stepId: 'wait',
              kind: 'Then',
              text: 'the queue eventually drains',
              stepNumber: 2,
              progress: {
                message: 'waiting… (attempt 4, 800ms); last value 2',
                row: null,
                totalRows: null,
                elapsedMs: 12_400,
              },
            }),
          ],
        }),
      },
    })

    const waiting = wrapper.find('[data-testid="waiting-label"]')
    expect(waiting.text()).toContain('waiting… (attempt 4, 800ms); last value 2')
    expect(wrapper.find('[data-testid="elapsed-label"]').text()).toBe('12s')
    expect(wrapper.find('[data-testid="row-label"]').exists()).toBe(false)
  })
})
