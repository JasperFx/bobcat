import { describe, expect, it } from 'vitest'
import { COLLAPSED_WIDTH, layoutEventModel } from '../layout'
import { LANE_ORDER } from '../types'
import { withdrawFundsModel } from './fixtures'

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

  it('handles a null or empty descriptor without throwing', () => {
    for (const empty of [null, undefined, { name: 'x' }, { name: 'x', slices: [] }]) {
      const graph = layoutEventModel(empty as never)
      expect(graph.nodes).toHaveLength(0)
      expect(graph.width).toBe(0)
      expect(graph.lanes).toHaveLength(4)
    }
  })
})
