import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import SpecificationView from '../SpecificationView.vue'
import type { ScenarioState } from '@/stores/runs-store'

/**
 * bobcat#784 — a scenario's reports (bobcat#408) rendered live, beneath its steps, as the results
 * page renders them: the same ReportGrid, the same cells.
 */
function scenario(reports: ScenarioState['reports']): ScenarioState {
  return {
    uid: 'Wallet/crediting', feature: 'Wallet', scenario: 'crediting', status: 'failed', attempt: 1, attempts: 1,
    scheduledAttempt: null, outcome: 'Failed', durationMs: 10, errorMessage: null, retryReason: null, steps: [],
    totalSteps: null, declaredSteps: [], plannedSteps: [], touchedTypes: [], finishedAt: null, state: null,
    workerPublished: true, forwarded: false, displayName: null, reports,
  }
}

describe('SpecificationView — reports', () => {
  it('renders each report as a titled grid, with the rows it could not show counted', () => {
    const wrapper = mount(SpecificationView, {
      props: {
        scenario: scenario([{
          title: 'Message activity', columns: ['Message', 'Outcome'], suppressedRows: 3,
          cells: [
            { name: 'Message', status: 'ok', expected: null, actual: null, note: null, rowIndex: 0, value: 'CreditWallet', comparison: null },
            { name: 'Outcome', status: 'ok', expected: null, actual: null, note: null, rowIndex: 0, value: 'handled', comparison: null },
            { name: 'Message', status: 'ok', expected: null, actual: null, note: null, rowIndex: 1, value: 'WalletCredited', comparison: null },
            { name: 'Outcome', status: 'failed', expected: 'handled', actual: 'dead-lettered', note: null, rowIndex: 1, value: null, comparison: null },
          ],
        }]),
      },
    })

    const report = wrapper.get('[data-testid="report"]')
    expect(report.find('h4').text()).toBe('Message activity')
    expect(report.findAll('th').map((th) => th.text())).toEqual(['Message', 'Outcome'])
    expect(report.findAll('tbody tr')).toHaveLength(2)
    expect(report.findAll('tbody tr')[1]!.findAll('td')[1]!.attributes('data-verdict')).toBe('failed')
    expect(report.text()).toContain('…and 3 more')
  })

  it('renders nothing for a scenario with no reports, and for a source that predates them', () => {
    expect(mount(SpecificationView, { props: { scenario: scenario([]) } }).find('[data-testid="report"]').exists()).toBe(false)
    const { reports: _omitted, ...older } = scenario([])
    expect(mount(SpecificationView, { props: { scenario: older } }).find('[data-testid="report"]').exists()).toBe(false)
  })
})
