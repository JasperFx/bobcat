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
      cardWidth,
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
