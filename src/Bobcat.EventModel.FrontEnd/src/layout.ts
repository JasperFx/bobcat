import { requiredContentWidth } from './text'
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
  /**
   * Minimum card width in px, and the width every column keeps unless its own labels need more
   * (issue bobcat#180). Set `maxCardWidth` to the same value to pin every card to one width.
   */
  cardWidth?: number
  /**
   * Ceiling a column may grow to for a long label. Past it the card clamps the label to
   * {@link MAX_LABEL_LINES} lines with an ellipsis and leaves the full text on the tooltip —
   * one pathological route must not push every other column off the screen.
   */
  maxCardWidth?: number
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
  /** Width every card in this column was given — sized to the column's own labels (#180). */
  cardWidth: number
  collapsed: boolean
}

/**
 * An edge with the polyline a viewer draws it as (issue #180's sibling, bobcat#181).
 *
 * Routed here rather than in the component for the same reason positions are: two viewers drawing
 * the same descriptor differently is the one thing this package exists to prevent, and a route is
 * as much a rendering claim as a coordinate. `points` is plot-space and always has at least two
 * entries; the last one is where the arrowhead goes.
 */
export interface LaidOutEdge extends EventModelEdge {
  points: ReadonlyArray<{ x: number; y: number }>
}

export interface EventModelGraph {
  nodes: LaidOutNode[]
  edges: LaidOutEdge[]
  lanes: LaidOutLane[]
  slices: LaidOutSlice[]
  width: number
  height: number
}

const DEFAULTS = {
  cardWidth: 180,
  maxCardWidth: 320,
  cardHeight: 72,
  gapX: 24,
  gapY: 48,
  sliceGap: 56
} as const

/** Width of the placeholder a collapsed slice column occupies. */
export const COLLAPSED_WIDTH = 48

/**
 * The card's own type scale and box, owned here rather than in the stylesheet (#180).
 *
 * A width computed from one font size and rendered at another is a clipped label with no
 * symptom anyone can trace, so `EventModelView` writes these onto the card as inline style
 * instead of letting CSS hold a second opinion.
 */
export const LABEL_FONT_SIZE = 13
export const LABEL_LINE_HEIGHT = 1.25
export const CARD_PADDING_X = 8
/** Lines a column is *sized* for; see `requiredContentWidth`. */
export const LABEL_TARGET_LINES = 2
/** Lines a card will *render* before clamping with an ellipsis. 3 × 1.25 × 13px + padding < 72. */
export const MAX_LABEL_LINES = 3

/**
 * The canvas chrome around the plot, here for the same reason the type scale is (#182): the zoom
 * wrapper has to know the *unscaled* size of everything it scales, and a stylesheet holding a
 * second opinion about the gutter's width is a scrollbar that stops half a lane-label short.
 */
export const GUTTER_WIDTH = 132
export const GUTTER_GAP = 12
export const CANVAS_PADDING = 12

/** Overall size of the drawn canvas, chrome included — what a zoom wrapper scales. */
export function canvasSize(graph: EventModelGraph): { width: number; height: number } {
  return {
    width: 2 * CANVAS_PADDING + GUTTER_WIDTH + GUTTER_GAP + graph.width,
    height: 2 * CANVAS_PADDING + graph.height
  }
}

/**
 * The width the cards in one column get: the widest label's requirement, held between the
 * caller's floor and ceiling. Pure — it estimates from the label text, never measures the DOM.
 *
 * A `Hotspot` label is excluded from the vote. The producer projects each hotspot into an element
 * whose label IS the hotspot text (jasperfx#704), so it is a whole sentence — *"EmittedEvents:
 * Derived claims ClaimResult; Declared claims NodeClaimed, ClaimRenewed"* — and letting prose set
 * the width of a column of type names widens every column on a real canvas to fit the finding
 * rather than the model. The sticky still wraps and clamps inside whatever width it is given, with
 * its full text on the tooltip, which is where a sentence belongs.
 */
function cardWidthFor(
  elements: readonly EventModelElement[],
  minCardWidth: number,
  maxCardWidth: number
): number {
  let needed = minCardWidth
  for (const element of elements) {
    if (element.kind === 'Hotspot') continue
    const content = requiredContentWidth(element.label ?? '', LABEL_FONT_SIZE, LABEL_TARGET_LINES)
    needed = Math.max(needed, Math.ceil(content) + 2 * CARD_PADDING_X)
  }
  return Math.min(needed, maxCardWidth)
}

/**
 * The polyline joining two cards.
 *
 * Two cases, because an Event Modeling canvas has exactly two kinds of relationship and they read
 * differently. Along a lane — command → handler → aggregate — the flow is left to right, so a
 * straight horizontal line between the facing edges is the whole story. Across lanes — command →
 * event, event → projection — the line has to cross a lane gap, and an orthogonal elbow through
 * the midpoint of that gap keeps it legible where a diagonal would cut through the band divider at
 * an arbitrary angle and read as a different kind of statement.
 *
 * Both cases are direction-aware: an edge pointing back up (or left) leaves the *other* side of
 * its source, so the arrowhead never lands on the face it started from.
 */
function routeEdge(from: LaidOutNode, to: LaidOutNode): { x: number; y: number }[] {
  const fromMidY = from.y + from.height / 2
  const toMidY = to.y + to.height / 2

  if (from.y === to.y) {
    return to.x >= from.x
      ? [
          { x: from.x + from.width, y: fromMidY },
          { x: to.x, y: toMidY }
        ]
      : [
          { x: from.x, y: fromMidY },
          { x: to.x + to.width, y: toMidY }
        ]
  }

  const downward = to.y > from.y
  const startY = downward ? from.y + from.height : from.y
  const endY = downward ? to.y : to.y + to.height
  const gapMidY = (startY + endY) / 2
  const fromMidX = from.x + from.width / 2
  const toMidX = to.x + to.width / 2

  const start = { x: fromMidX, y: startY }
  const end = { x: toMidX, y: endY }
  // A card sitting directly above or below its partner wants one straight drop, not an elbow with
  // two zero-length legs.
  if (fromMidX === toMidX) return [start, end]

  return [start, { x: fromMidX, y: gapMidY }, { x: toMidX, y: gapMidY }, end]
}

export function layoutEventModel(
  descriptor: EventModelDescriptor | null | undefined,
  options: LayoutOptions = {}
): EventModelGraph {
  const minCardWidth = options.cardWidth ?? DEFAULTS.cardWidth
  const maxCardWidth = Math.max(minCardWidth, options.maxCardWidth ?? DEFAULTS.maxCardWidth)
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
  const edges: LaidOutEdge[] = []

  let cursorX = 0

  for (const slice of descriptor?.slices ?? []) {
    const isCollapsed = collapsed.has(slice.name)
    const elements = isCollapsed ? [] : (slice.elements ?? [])

    // Group by lane, preserving declaration order inside each lane. Declaration order is the
    // producer's statement about sequence (a command before the events it emits), so sorting
    // here would discard information the descriptor deliberately carries.
    // Laid-out nodes of THIS slice, by element id — what the slice's own edges are routed
    // against. Per slice because ids are unique per slice and an edge never crosses one.
    const placed = new Map<string, LaidOutNode>()

    const byLane = new Map<EventModelLane, EventModelElement[]>()
    for (const element of elements) {
      const bucket = byLane.get(element.lane)
      if (bucket) bucket.push(element)
      else byLane.set(element.lane, [element])
    }

    // One card width per slice column, sized to that column's own labels (#180). Per column and
    // not per card: cards in a column line up under each other, and a lane of ragged widths reads
    // as a broken grid rather than as "this name is longer". Neighbouring columns may differ —
    // they are separated by a slice divider, which is where a width change is legible.
    const cardWidth = isCollapsed ? minCardWidth : cardWidthFor(elements, minCardWidth, maxCardWidth)

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
        const node: LaidOutNode = {
          id: element.id,
          element,
          sliceName: slice.name,
          x: cursorX + index * (cardWidth + gapX),
          y: top + gapY / 2,
          width: cardWidth,
          height: cardHeight
        }
        nodes.push(node)
        placed.set(node.id, node)
      })
    }

    slices.push({
      name: slice.name,
      descriptor: slice,
      x: cursorX,
      width: sliceWidth,
      cardWidth,
      collapsed: isCollapsed
    })

    if (!isCollapsed) {
      // Edges reference elements by id and never cross a slice, so an edge whose endpoints are
      // not both present is dropped. A dangling edge is a producer bug; rendering it as a line
      // to the origin would make it look like a modelling statement.
      for (const edge of slice.edges ?? []) {
        const from = placed.get(edge.fromId)
        const to = placed.get(edge.toId)
        // An element in an unknown lane was dropped above, so "placed" — not "declared" — is the
        // right test: an edge to a card nobody drew has nowhere to point either.
        if (from && to) edges.push({ ...edge, points: routeEdge(from, to) })
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
