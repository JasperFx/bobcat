import {
  LANE_ORDER,
  type EventModelDescriptor,
  type EventModelEdge,
  type EventModelElement,
  type EventModelLane,
  type EventModelSliceDescriptor
} from './types'

/**
 * Deterministic grid layout for an Event Model descriptor.
 *
 * Pure, synchronous, and free of any graph library — no elk, no worker, no measurement pass,
 * so the canvas renders in one pass and a test can assert exact coordinates. That determinism
 * is the point: the acceptance criterion for this package is that the same descriptor renders
 * identically in the Bobcat console and in CritterWatch, and "identically" is only checkable
 * if position is a function of the descriptor alone.
 *
 * The shape is the standard Event Modeling canvas: slices are vertical columns in declaration
 * order, lanes are horizontal bands in canonical top-to-bottom order, and an element sits in
 * the cell where its slice column meets its lane band. Several elements in one cell run
 * left-to-right, which is what widens a slice with three emitted events.
 */

export interface LayoutOptions {
  /** Card width in px. */
  cardWidth?: number
  /** Card height in px. */
  cardHeight?: number
  /** Horizontal gap between cards inside a lane cell. */
  gapX?: number
  /** Vertical gap between lane bands. */
  gapY?: number
  /** Gap between slice columns, on top of the inter-card gap. */
  sliceGap?: number
  /** Slice names to collapse to a single placeholder column. */
  collapsedSlices?: ReadonlySet<string>
}

export interface LaidOutNode {
  id: string
  element: EventModelElement
  /** Slice this element belongs to. Edges never cross slices, but the viewer groups by it. */
  sliceName: string
  x: number
  y: number
  width: number
  height: number
}

export interface LaidOutLane {
  lane: EventModelLane
  y: number
  height: number
}

export interface LaidOutSlice {
  name: string
  descriptor: EventModelSliceDescriptor
  x: number
  width: number
  collapsed: boolean
}

export interface EventModelGraph {
  nodes: LaidOutNode[]
  edges: EventModelEdge[]
  lanes: LaidOutLane[]
  slices: LaidOutSlice[]
  width: number
  height: number
}

const DEFAULTS = {
  cardWidth: 180,
  cardHeight: 72,
  gapX: 24,
  gapY: 48,
  sliceGap: 56
} as const

/** Width of the placeholder a collapsed slice column occupies. */
export const COLLAPSED_WIDTH = 48

export function layoutEventModel(
  descriptor: EventModelDescriptor | null | undefined,
  options: LayoutOptions = {}
): EventModelGraph {
  const cardWidth = options.cardWidth ?? DEFAULTS.cardWidth
  const cardHeight = options.cardHeight ?? DEFAULTS.cardHeight
  const gapX = options.gapX ?? DEFAULTS.gapX
  const gapY = options.gapY ?? DEFAULTS.gapY
  const sliceGap = options.sliceGap ?? DEFAULTS.sliceGap
  const collapsed = options.collapsedSlices ?? new Set<string>()

  const laneHeight = cardHeight + gapY
  const lanes: LaidOutLane[] = LANE_ORDER.map((lane, index) => ({
    lane,
    y: index * laneHeight,
    height: laneHeight
  }))
  const laneTop = new Map(lanes.map((l) => [l.lane, l.y]))

  const nodes: LaidOutNode[] = []
  const slices: LaidOutSlice[] = []
  const edges: EventModelEdge[] = []

  let cursorX = 0

  for (const slice of descriptor?.slices ?? []) {
    const isCollapsed = collapsed.has(slice.name)
    const elements = isCollapsed ? [] : (slice.elements ?? [])

    // Group by lane, preserving declaration order inside each lane. Declaration order is the
    // producer's statement about sequence (a command before the events it emits), so sorting
    // here would discard information the descriptor deliberately carries.
    const byLane = new Map<EventModelLane, EventModelElement[]>()
    for (const element of elements) {
      const bucket = byLane.get(element.lane)
      if (bucket) bucket.push(element)
      else byLane.set(element.lane, [element])
    }

    const widest = Math.max(1, ...[...byLane.values()].map((b) => b.length))
    const sliceWidth = isCollapsed
      ? COLLAPSED_WIDTH
      : widest * cardWidth + (widest - 1) * gapX

    for (const [lane, bucket] of byLane) {
      const top = laneTop.get(lane)
      // A lane the contract does not know about is dropped rather than stacked at y=0, where it
      // would silently overlap the wireframe lane and read as a rendering bug rather than as
      // "this descriptor came from a newer JasperFx than this package".
      if (top === undefined) continue

      bucket.forEach((element, index) => {
        nodes.push({
          id: element.id,
          element,
          sliceName: slice.name,
          x: cursorX + index * (cardWidth + gapX),
          y: top + gapY / 2,
          width: cardWidth,
          height: cardHeight
        })
      })
    }

    slices.push({
      name: slice.name,
      descriptor: slice,
      x: cursorX,
      width: sliceWidth,
      collapsed: isCollapsed
    })

    if (!isCollapsed) {
      // Edges reference elements by id and never cross a slice, so an edge whose endpoints are
      // not both present is dropped. A dangling edge is a producer bug; rendering it as a line
      // to the origin would make it look like a modelling statement.
      const present = new Set(elements.map((e) => e.id))
      for (const edge of slice.edges ?? []) {
        if (present.has(edge.fromId) && present.has(edge.toId)) edges.push(edge)
      }
    }

    cursorX += sliceWidth + sliceGap
  }

  return {
    nodes,
    edges,
    lanes,
    slices,
    width: Math.max(0, cursorX - sliceGap),
    height: lanes.length * laneHeight
  }
}
