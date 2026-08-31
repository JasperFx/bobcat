import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import EventModelView from '../EventModelView.vue'
import { fourSourceModel, withdrawFundsModel } from './fixtures'

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

  it('emits the clicked slice so a host can open its bound scenarios (#108)', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const name = wrapper.findAll('.em-slice-name').find((s) => s.text() === 'WithdrawFunds')!
    name.trigger('click')
    const emitted = wrapper.emitted('slice-click')![0][0]
    expect(emitted).toMatchObject({ name: 'WithdrawFunds' })
  })

  it('gives a long label break opportunities instead of clipping it (#180)', () => {
    // A PascalCase name is one unbreakable word to a browser, so the fixed-width card simply cut
    // it off. The label is rendered in segments separated by <wbr>, which is what lets it wrap.
    const model = withdrawFundsModel()
    model.slices![0].elements![1].label = 'DepositMoneyIntoAccount'
    const wrapper = mount(EventModelView, { props: { descriptor: model } })
    const label = wrapper
      .findAll('.em-card-label')
      .find((l) => l.text() === 'DepositMoneyIntoAccount')!
    expect(label.findAll('wbr')).toHaveLength(4)
    // The text itself is untouched — the breaks are opportunities, not inserted characters.
    expect(label.text()).toBe('DepositMoneyIntoAccount')
  })

  it('renders the card at the type scale the width was computed from', () => {
    // layout.ts owns the type scale; a stylesheet with a second opinion about it is a clipped
    // label with no traceable symptom.
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const style = wrapper.find('.em-card').attributes('style')!
    expect(style).toContain('font-size: 13px')
    expect(style).toContain('padding: 6px 8px')
  })

  it('keeps a long slice name inside its own column, with the full name on the title', () => {
    const model = withdrawFundsModel()
    model.slices![0].name = 'WithdrawFundsFromAnInternationalAccount'
    const wrapper = mount(EventModelView, { props: { descriptor: model } })
    const name = wrapper.find('.em-slice-name')
    expect(name.attributes('title')).toBe('WithdrawFundsFromAnInternationalAccount')
    expect(name.attributes('style')).toContain('max-width: 580px')
  })

  it('draws a polyline per edge, behind the cards and pointer-inert (#181)', () => {
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    const lines = wrapper.findAll('.em-edge')
    expect(lines).toHaveLength(4)
    const handlerEdge = lines.find(
      (l) => l.attributes('data-to') === 'WithdrawFunds/Handler/Bank.AccountHandler'
    )!
    expect(handlerEdge.attributes('points')).toBe('180,180 204,180')
    expect(handlerEdge.attributes('marker-end')).toBe('url(#em-arrow)')
  })

  it('draws nothing for an edge whose endpoints were not both drawn', () => {
    const model = withdrawFundsModel()
    model.slices![0].edges!.push({
      fromId: 'WithdrawFunds/Command/Bank.WithdrawFunds',
      toId: 'nope'
    })
    const wrapper = mount(EventModelView, { props: { descriptor: model } })
    // A line to nowhere would read as a modelling claim rather than the producer bug it is.
    expect(wrapper.findAll('.em-edge')).toHaveLength(4)
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

/**
 * jasperfx#703 / #704 — the canvas draws WHERE a claim came from, and draws a dropped claim as a
 * finding rather than as another magenta sticky.
 */
describe('EventModelView — provenance and source disagreement', () => {
  function cards() {
    return mount(EventModelView, { props: { descriptor: fourSourceModel() } }).findAll('.em-card')
  }

  it('stamps the ladder rung on every card that carries one', () => {
    const byLabel = new Map(cards().map((c) => [c.text(), c.attributes('data-provenance')]))

    expect(byLabel.get('Teller screen')).toBe('Declared')
    expect(byLabel.get('WithdrawFunds')).toBe('Derived')
    expect(byLabel.get('FundsWithdrawn')).toBe('Observed')
    expect(byLabel.get('AuditRecorded')).toBe('Observed')
  })

  it('leaves the fill colour alone — it means the KIND, in every viewer', () => {
    // The second visual channel must not touch the first: two viewers agreeing on what a colour
    // means is the whole reason this package exists, so an Observed event is still event-orange.
    const observed = cards().find((c) => c.text() === 'FundsWithdrawn')!
    expect(observed.attributes('style')).toContain('background: #F5A623')
  })

  it('omits the attribute entirely on a descriptor from a pre-2.56 producer', () => {
    // Absent, not 'Declared' — the viewer must not invent a rung nobody claimed.
    const wrapper = mount(EventModelView, { props: { descriptor: withdrawFundsModel() } })
    expect(
      wrapper.findAll('.em-card').every((c) => c.attributes('data-provenance') === undefined)
    ).toBe(true)
  })

  it('marks a source disagreement apart from a pending-specification hotspot', () => {
    const all = cards()
    const pending = all.find((c) => c.text() === 'overdraft not specified')!
    const conflict = all.find((c) => c.text().startsWith('EmittedEvents:'))!

    expect(pending.attributes('data-hotspot-origin')).toBe('PendingSpecification')
    expect(conflict.attributes('data-hotspot-origin')).toBe('SourceDisagreement')
  })

  it('puts both claims on the tooltip, so the reader can decide which source to fix', () => {
    const conflict = cards().find((c) => c.text().startsWith('EmittedEvents:'))!
    const title = conflict.attributes('title')!

    expect(title).toContain('Kept: Observed claims FundsWithdrawn, AuditRecorded')
    expect(title).toContain('Dropped: Derived claims FundsWithdrawn')
  })

  it('explains the rung on the tooltip of an ordinary card', () => {
    const observed = cards().find((c) => c.text() === 'AuditRecorded')!
    const title = observed.attributes('title')!

    expect(title).toContain('Bank.AuditRecorded')
    expect(title).toContain('seen happening in a running system')
  })

  it('does not mark a non-hotspot card with a hotspot origin', () => {
    expect(
      cards()
        .filter((c) => c.attributes('data-kind') !== 'Hotspot')
        .every((c) => c.attributes('data-hotspot-origin') === undefined)
    ).toBe(true)
  })
})
