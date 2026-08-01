import { describe, expect, it } from 'vitest'
import { layoutDag } from '../dagLayout'

describe('dagLayout', () => {
  it('a node always sits in a column right of everything it depends on', () => {
    const layout = layoutDag([
      { id: 'a', dependsOn: [] },
      { id: 'b', dependsOn: ['a'] },
      { id: 'c', dependsOn: ['a', 'b'] },
      { id: 'd', dependsOn: [] },
      { id: 'e', dependsOn: ['d', 'c'] },
    ])

    const col = new Map(layout.nodes.map((n) => [n.id, n.col]))
    for (const edge of layout.edges) {
      expect(col.get(edge.from)!).toBeLessThan(col.get(edge.to)!)
    }
    expect(col.get('c')).toBe(2) // longest path, not shortest: a -> b -> c
    expect(layout.cols).toBe(4)
  })

  it('every node gets a unique cell and rows count the tallest column', () => {
    const layout = layoutDag([
      { id: 'a', dependsOn: [] },
      { id: 'b', dependsOn: [] },
      { id: 'c', dependsOn: [] },
      { id: 'd', dependsOn: ['a'] },
    ])

    const cells = new Set(layout.nodes.map((n) => `${n.col}:${n.row}`))
    expect(cells.size).toBe(4)
    expect(layout.rows).toBe(3) // the root column holds a, b, c
  })

  it('the release-train shape lays out in narrative order', () => {
    // The epic's own plan shape: issue -> publish -> consume -> issue -> gate.
    const layout = layoutDag([
      { id: 'sqlite-store', dependsOn: [] },
      { id: 'publish', dependsOn: ['sqlite-store'] },
      { id: 'consume', dependsOn: ['publish'] },
      { id: 'context', dependsOn: ['consume'] },
      { id: 'gate', dependsOn: ['context'] },
    ])

    expect(layout.cols).toBe(5)
    expect(layout.rows).toBe(1)
    expect(layout.edges).toHaveLength(4)
  })

  it('survives unknown dependency ids and cycles rather than crashing the view', () => {
    const layout = layoutDag([
      { id: 'a', dependsOn: ['ghost'] }, // stale payload — server-side validation is upstream
      { id: 'b', dependsOn: ['c'] },
      { id: 'c', dependsOn: ['b'] },
    ])

    expect(layout.nodes).toHaveLength(3)
    // The ghost edge is dropped, not drawn to nowhere.
    expect(layout.edges.every((e) => e.from !== 'ghost')).toBe(true)
  })

  it('an empty plan is an empty board', () => {
    const layout = layoutDag([])
    expect(layout).toEqual({ nodes: [], edges: [], cols: 0, rows: 0 })
  })
})
