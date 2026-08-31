import { describe, expect, it } from 'vitest'
import { COLLAPSED_WIDTH, layoutEventModel } from '../layout'
import { LANE_ORDER } from '../types'
import { withdrawFundsModel } from './fixtures'

/** The withdrawal model with one label replaced — the #180 sizing cases in one place. */
function longLabelModel(label: string) {
  const model = withdrawFundsModel()
  model.slices![0].elements = [
    { id: 'WithdrawFunds/Trigger/long', kind: 'Trigger', lane: 'Wireframe', label }
  ]
  model.slices![0].edges = []
  return model
}

/**
 * The layout is the part that has to be *identical* between the Bobcat console and CritterWatch,
 * so it is pure and asserted on exact coordinates rather than on "looks about right".
 */
describe('layoutEventModel', () => {
  it('places every element of every slice', () => {
    const graph = layoutEventModel(withdrawFundsModel())
    expect(graph.nodes).toHaveLength(9)
    expect(graph.slices.map((s) => s.name)).toEqual(['WithdrawFunds', 'AccountBalance'])
  })

  it('always emits the four canonical lanes, top to bottom, even when a lane is empty', () => {
    // The AccountBalance slice has nothing in the Wireframe or Command lanes; the bands still
    // exist, because a canvas that reflows its lanes per slice is unreadable.
    const graph = layoutEventModel(withdrawFundsModel())
    expect(graph.lanes.map((l) => l.lane)).toEqual([...LANE_ORDER])
    expect(graph.lanes.map((l) => l.y)).toEqual([0, 120, 240, 360])
  })

  it('puts an element in the band for its lane', () => {
    const graph = layoutEventModel(withdrawFundsModel())
    const y = (id: string) => graph.nodes.find((n) => n.id === id)!.y
    expect(y('WithdrawFunds/Trigger/Teller screen')).toBe(24)
    expect(y('WithdrawFunds/Command/Bank.WithdrawFunds')).toBe(144)
    expect(y('WithdrawFunds/Event/Bank.FundsWithdrawn')).toBe(264)
    expect(y('AccountBalance/ReadModel/Bank.Balance')).toBe(384)
  })

  it('runs several elements in one lane cell left to right', () => {
    const graph = layoutEventModel(withdrawFundsModel())
    const xs = graph.nodes
      .filter((n) => n.sliceName === 'WithdrawFunds' && n.element.lane === 'Command')
      .map((n) => n.x)
    expect(xs).toEqual([0, 204, 408])
  })

  it('preserves declaration order inside a lane rather than sorting', () => {
    // Declaration order is the producer's statement about sequence — a command before the events
    // it emits. Sorting by label would silently discard that.
    const graph = layoutEventModel(withdrawFundsModel())
    const labels = graph.nodes
      .filter((n) => n.sliceName === 'WithdrawFunds' && n.element.lane === 'EventStream')
      .sort((a, b) => a.x - b.x)
      .map((n) => n.element.label)
    expect(labels).toEqual(['FundsWithdrawn', 'AccountOverdrawn'])
  })

  it('sizes a slice column to its widest lane and offsets the next slice past it', () => {
    const graph = layoutEventModel(withdrawFundsModel())
    // WithdrawFunds' widest lane holds 3 cards: 3*180 + 2*24 = 588.
    expect(graph.slices[0].width).toBe(588)
    expect(graph.slices[1].x).toBe(588 + 56)
  })

  it('is deterministic — the same descriptor lays out identically twice', () => {
    // This is the package's whole reason to exist: one descriptor, one picture, in two viewers.
    expect(layoutEventModel(withdrawFundsModel())).toEqual(layoutEventModel(withdrawFundsModel()))
  })

  it('drops an edge whose endpoints are not both present', () => {
    const model = withdrawFundsModel()
    model.slices![0].edges!.push({ fromId: 'WithdrawFunds/Command/Bank.WithdrawFunds', toId: 'nope' })
    const graph = layoutEventModel(model)
    // A dangling edge is a producer bug; drawing a line to nowhere would read as a modelling claim.
    expect(graph.edges.some((e) => e.toId === 'nope')).toBe(false)
    expect(graph.edges).toHaveLength(4)
  })

  it('collapses a named slice to a placeholder and keeps the rest laid out', () => {
    const graph = layoutEventModel(withdrawFundsModel(), {
      collapsedSlices: new Set(['WithdrawFunds'])
    })
    expect(graph.slices[0].collapsed).toBe(true)
    expect(graph.slices[0].width).toBe(COLLAPSED_WIDTH)
    expect(graph.nodes.every((n) => n.sliceName === 'AccountBalance')).toBe(true)
    expect(graph.slices[1].x).toBe(COLLAPSED_WIDTH + 56)
  })

  it('ignores an element in a lane this package does not know', () => {
    // A descriptor from a newer JasperFx must not silently stack an unknown lane at y=0, where it
    // would overlap the wireframe lane and read as a rendering bug.
    const model = withdrawFundsModel()
    model.slices![0].elements!.push({
      id: 'WithdrawFunds/Event/Future',
      kind: 'Event',
      lane: 'Speculative' as never,
      label: 'Future'
    })
    expect(layoutEventModel(model).nodes.some((n) => n.id === 'WithdrawFunds/Event/Future')).toBe(false)
  })

  it('keeps the default width for ordinary type names', () => {
    // The floor is the common case: nothing on this fixture is long enough to want more room, so
    // the canvas looks exactly as it did before #180.
    const graph = layoutEventModel(withdrawFundsModel())
    expect(graph.slices.map((s) => s.cardWidth)).toEqual([180, 180])
    expect(graph.nodes.every((n) => n.width === 180)).toBe(true)
  })

  it('widens a column whose label will not fit two lines at the default width (#180)', () => {
    // The bug was an absolutely-sized card with overflow:hidden — a long route was cut off. The
    // card now wraps at its break opportunities first, and the column only grows when two lines
    // at 180px still are not enough.
    const model = longLabelModel('PUT /api/organizations/{orgId}/subscriptions/{id}/cancel')
    const graph = layoutEventModel(model)
    expect(graph.slices[0].cardWidth).toBe(192)
    expect(graph.nodes[0].width).toBe(192)
    // The neighbouring column keeps its own width, and is pushed right by exactly the difference.
    expect(graph.slices[1].cardWidth).toBe(180)
    expect(graph.slices[1].x).toBe(192 + 56)
  })

  it('caps a column at maxCardWidth rather than letting one label own the canvas', () => {
    const model = longLabelModel('RegisterCustomerAccountForInternationalWireTransferProcessing')
    expect(layoutEventModel(model).slices[0].cardWidth).toBe(214)
    // Past the cap the card clamps the label and keeps the full text on its tooltip.
    expect(layoutEventModel(model, { maxCardWidth: 200 }).slices[0].cardWidth).toBe(200)
  })

  it('pins every card to one width when maxCardWidth equals cardWidth', () => {
    // The escape hatch for a consumer that wants the old fixed grid back.
    const model = longLabelModel('RegisterCustomerAccountForInternationalWireTransferProcessing')
    const graph = layoutEventModel(model, { cardWidth: 180, maxCardWidth: 180 })
    expect(graph.slices.map((s) => s.cardWidth)).toEqual([180, 180])
  })

  it('does not let a hotspot sentence set the width of a column of type names', () => {
    // A hotspot's label IS its text (jasperfx#704) — prose, not a type name. Sizing to it widened
    // every column on a real canvas to fit the finding instead of the model.
    const model = longLabelModel('ClaimNode')
    model.slices![0].elements!.push({
      id: 'WithdrawFunds/Hotspot/disagreement',
      kind: 'Hotspot',
      lane: 'EventStream',
      label: 'EmittedEvents: Derived claims ClaimResult; Declared claims NodeClaimed, ClaimRenewed'
    })
    expect(layoutEventModel(model).slices[0].cardWidth).toBe(180)
  })

  it('routes an edge along a lane as a straight line between the facing card edges (#181)', () => {
    const graph = layoutEventModel(withdrawFundsModel())
    const edge = graph.edges.find(
      (e) => e.toId === 'WithdrawFunds/Handler/Bank.AccountHandler'
    )!
    // Command at x 0..180, handler at x 204..384, both mid-band at y 180.
    expect(edge.points).toEqual([
      { x: 180, y: 180 },
      { x: 204, y: 180 }
    ])
  })

  it('routes an edge across lanes as an elbow through the middle of the lane gap', () => {
    const graph = layoutEventModel(withdrawFundsModel())
    const edge = graph.edges.find((e) => e.toId === 'WithdrawFunds/Event/Bank.FundsWithdrawn')!
    // Handler bottom (216) down to the event's top (264), turning at the gap's midpoint. A
    // diagonal would cross the band divider at an arbitrary angle and read as a different claim.
    expect(edge.points).toEqual([
      { x: 294, y: 216 },
      { x: 294, y: 240 },
      { x: 90, y: 240 },
      { x: 90, y: 264 }
    ])
  })

  it('drops the elbow when one card sits directly above the other', () => {
    const graph = layoutEventModel(withdrawFundsModel())
    const edge = graph.edges.find(
      (e) => e.toId === 'AccountBalance/Projection/Bank.BalanceProjection'
    )!
    expect(edge.points).toEqual([
      { x: 734, y: 336 },
      { x: 734, y: 384 }
    ])
  })

  it('leaves the other face of the source when the edge points backwards', () => {
    // Declaration order is the producer's, and nothing says a slice declares its elements in
    // flow order — an edge pointing back up or left must not start on the face it ends at.
    const model = withdrawFundsModel()
    model.slices![0].edges = [
      {
        fromId: 'WithdrawFunds/Handler/Bank.AccountHandler',
        toId: 'WithdrawFunds/Command/Bank.WithdrawFunds'
      }
    ]
    const [edge] = layoutEventModel(model).edges
    expect(edge.points).toEqual([
      { x: 204, y: 180 },
      { x: 180, y: 180 }
    ])
  })

  it('handles a null or empty descriptor without throwing', () => {
    for (const empty of [null, undefined, { name: 'x' }, { name: 'x', slices: [] }]) {
      const graph = layoutEventModel(empty as never)
      expect(graph.nodes).toHaveLength(0)
      expect(graph.width).toBe(0)
      expect(graph.lanes).toHaveLength(4)
    }
  })
})
