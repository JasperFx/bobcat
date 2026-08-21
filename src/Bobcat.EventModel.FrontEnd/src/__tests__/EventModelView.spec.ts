import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import EventModelView from '../EventModelView.vue'
import { withdrawFundsModel } from './fixtures'

describe('EventModelView', () => {
  it('renders one card per element with its canonical colour', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const cards = wrapper.findAll('.em-card')
    expect(cards).toHaveLength(9)

    const event = cards.find((c) => c.text() === 'FundsWithdrawn')!
    expect(event.attributes('style')).toContain('background: #F5A623')
  })

  it('draws an outlined kind with a transparent fill so it stays distinct from its command', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const handler = wrapper.findAll('.em-card').find((c) => c.text() === 'AccountHandler')!
    expect(handler.attributes('style')).toContain('background: transparent')
  })

  it('shows an empty state rather than a blank canvas', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: { name: 'x', slices: [] } } })
    expect(wrapper.find('[data-testid="event-model-empty"]').exists()).toBe(true)
    expect(wrapper.findAll('.em-card')).toHaveLength(0)
  })

  it('tolerates a null descriptor', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: null } })
    expect(wrapper.find('[data-testid="event-model-empty"]').exists()).toBe(true)
  })

  it('emits the clicked element so a host can drill down to the bound spec', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const card = wrapper.findAll('.em-card').find((c) => c.text() === 'WithdrawFunds')!
    card.trigger('click')
    expect(wrapper.emitted('element-click')![0][0]).toMatchObject({
      kind: 'Command',
      label: 'WithdrawFunds'
    })
  })

  it('renders the four lane captions', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    expect(wrapper.findAll('.em-lane-label').map((l) => l.text())).toEqual([
      'Wireframe / Trigger',
      'Command',
      'Event Stream',
      'Read Model'
    ])
  })

  it('colours a slice from run evidence keyed by spec identity (#107)', () => {
    const wrapper = mount(EventModelView, {
      props: {
        descriptor: withdrawFundsModel(),
        sliceOutcomes: { 'Withdraw Funds/a withdrawal succeeds': 'failed' }
      }
    })
    const slice = wrapper.find('[data-slice="WithdrawFunds"]')
    expect(slice.attributes('data-outcome')).toBe('failed')
    // A slice no run evidence named stays unmarked rather than defaulting to green.
    expect(wrapper.find('[data-slice="AccountBalance"]').attributes('data-outcome')).toBeUndefined()
  })

  it('hides a collapsed slice cards but keeps its column', () => {
    const wrapper = mount(EventModelView, {
      props: { descriptor: withdrawFundsModel(), collapsedSlices: new Set(['WithdrawFunds']) }
    })
    expect(wrapper.findAll('.em-card')).toHaveLength(3)
    expect(wrapper.find('[data-slice="WithdrawFunds"]').exists()).toBe(true)
  })
})
