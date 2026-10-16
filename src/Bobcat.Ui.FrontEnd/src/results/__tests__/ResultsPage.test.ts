import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import type { BobcatResults } from '@/messages/results-file'
import ResultsPage from '../ResultsPage.vue'
import fixture from './results.fixture.json'

const results = fixture as BobcatResults

function page(error: string | null = null) {
  return mount(ResultsPage, { props: { data: { files: [{ attachmentBase: '', results }] }, error } })
}

describe('the HTML results page', () => {
  it('says whether the run succeeded, and what it counted', () => {
    const wrapper = page()
    expect(wrapper.get('[data-testid="run-verdict"]').text()).toBe('Failed')
    expect(wrapper.get('[data-testid="run-counts"]').text()).toContain('6 specifications: 1 passed, 4 failed, 1 acceptance not yet passing')
  })

  it('opens the scenarios that fail the run and leaves the rest closed', () => {
    const scenarios = page().findAll('[data-testid="scenario"]')
    const open = scenarios.filter((s) => (s.element as unknown as { open: boolean }).open).map((s) => s.attributes('id'))
    expect(open).toEqual([
      'Calculator/an exception in an action stops the scenario',
      'Calculator/asserting values',
      'Calculator, narrated/the run throws before the next action',
      'Sets/a set of names missing one and expecting one that is not there',
    ])
  })

  it('renders each scenario through the board\'s specification view', () => {
    const wrapper = page()
    const sets = wrapper.get('[id="Sets/a set of names missing one and expecting one that is not there"]')
    expect(sets.find('[data-testid="specification"]').exists()).toBe(true)
    expect(sets.findAll('[data-testid="row-marker"]').map((m) => m.text())).toEqual(['', '', 'missing', 'extra'])
  })

  it('renders reports, logs and attachments', () => {
    const wrapper = page()
    const logged = wrapper.get('[id="Actions/custom logging from a step"]')
    expect(logged.get('[data-testid="report"]').text()).toContain('Envelopes')
    expect(logged.get('[data-testid="scenario-logs"]').text()).toContain('between steps: 12 rows')

    const thrown = wrapper.get('[id="Calculator/an exception in an action stops the scenario"]')
    expect(thrown.get('img').attributes('src')).toBe('attachments/run/spec/01-screen.png')
    expect(thrown.get('[data-testid="attachment-not-kept"]').text()).toBe('over the 10 MB cap')
  })

  it('filters to the failing specifications', async () => {
    const wrapper = page()
    await wrapper.get('[data-testid="failing-only"]').setValue(true)
    const ids = wrapper.findAll('[data-testid="scenario"]').map((s) => s.attributes('id'))
    expect(ids).not.toContain('Actions/custom logging from a step')
    expect(ids).toContain('Acceptance specifications/a wrong is gathered before the behaviour is finished')
  })

  it('says when its results could not be read', () => {
    expect(page('broken').get('[data-testid="page-error"]').text()).toBe('broken')
  })
})
