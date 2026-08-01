/**
 * Layered DAG layout for plan graphs — pure and deterministic on purpose: cards get fixed
 * grid coordinates, so the SVG edge layer can be drawn from arithmetic instead of DOM
 * measurement. Plans are tens of nodes, not thousands; longest-path layering plus one
 * barycenter pass is plenty, and a dependency edge always points left-to-right.
 */

export interface DagNode {
  id: string
  dependsOn: string[]
}

export interface PlacedNode {
  id: string
  /** Column = dependency depth: a node always sits right of everything it depends on. */
  col: number
  row: number
}

export interface DagEdge {
  from: string
  to: string
}

export interface DagLayout {
  nodes: PlacedNode[]
  edges: DagEdge[]
  cols: number
  /** Tallest column's node count — the grid's height. */
  rows: number
}

export function layoutDag(input: DagNode[]): DagLayout {
  const byId = new Map(input.map((n) => [n.id, n]))
  const layer = new Map<string, number>()

  // Longest path from the roots. Unknown dependency ids are ignored defensively — the
  // server validates plans, but a stale status payload must not crash the view. The
  // visiting set breaks cycles for the same reason.
  const visiting = new Set<string>()
  function layerOf(id: string): number {
    const known = layer.get(id)
    if (known !== undefined) return known
    if (visiting.has(id)) return 0

    visiting.add(id)
    const node = byId.get(id)
    const deps = (node?.dependsOn ?? []).filter((d) => byId.has(d))
    const value = deps.length === 0 ? 0 : 1 + Math.max(...deps.map(layerOf))
    visiting.delete(id)

    layer.set(id, value)
    return value
  }

  for (const node of input) layerOf(node.id)

  const cols = input.length === 0 ? 0 : 1 + Math.max(...input.map((n) => layer.get(n.id)!))

  // Column by column: the first column keeps document order (the author's narrative);
  // later columns sort by the average row of their dependencies so edges stay short,
  // with document order as the stable tiebreak.
  const row = new Map<string, number>()
  const placed: PlacedNode[] = []
  let rows = 0

  for (let col = 0; col < cols; col++) {
    const members = input.filter((n) => layer.get(n.id) === col)

    const keyed = members.map((n, index) => {
      const depRows = n.dependsOn.filter((d) => row.has(d)).map((d) => row.get(d)!)
      const barycenter = depRows.length === 0 ? index : depRows.reduce((a, b) => a + b, 0) / depRows.length
      return { n, index, barycenter }
    })
    keyed.sort((a, b) => a.barycenter - b.barycenter || a.index - b.index)

    keyed.forEach((k, r) => {
      row.set(k.n.id, r)
      placed.push({ id: k.n.id, col, row: r })
    })

    rows = Math.max(rows, members.length)
  }

  const edges: DagEdge[] = input.flatMap((n) =>
    n.dependsOn.filter((d) => byId.has(d)).map((d) => ({ from: d, to: n.id })),
  )

  return { nodes: placed, edges, cols, rows }
}
