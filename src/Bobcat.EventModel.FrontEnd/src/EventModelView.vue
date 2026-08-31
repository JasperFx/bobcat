<script setup lang="ts">
/**
 * Renders a JasperFx.Events `EventModelDescriptor` as an Event Modeling canvas.
 *
 * Deliberately renders the *descriptor* rather than any producer's private model. CritterWatch's
 * original `EventModelingView.vue` took its runtime `Lifecycle` and transformed it in the
 * component; that made the component unshareable, because the Bobcat console has no Lifecycle
 * and CritterWatch has no Bobcat generator. With jasperfx#687 the descriptor carries `elements`
 * and `edges` as "the rendering contract every viewer can draw from without a second transform",
 * so each producer adapts to the descriptor once and this component stays common.
 *
 * Layout is the pure grid from `layout.ts` — synchronous, no elk, no measurement pass.
 */
import { computed, nextTick, onBeforeUnmount, ref } from 'vue'
import {
  CANVAS_PADDING,
  CARD_PADDING_X,
  GUTTER_GAP,
  GUTTER_WIDTH,
  canvasSize,
  LABEL_FONT_SIZE,
  LABEL_LINE_HEIGHT,
  MAX_LABEL_LINES,
  layoutEventModel,
  type LayoutOptions
} from './layout'
import { segmentLabel } from './text'
import { TRIGGER_ICON, TRIGGER_KIND_LABEL, parseRoute } from './icons'
import { colorFor, inkFor, DASHED_KINDS, OUTLINED_KINDS } from './palette'
import {
  LANE_LABEL,
  PROVENANCE_LABEL,
  type EventModelDescriptor,
  type EventModelElement,
  type EventModelSliceDescriptor,
  type HotspotDescriptor
} from './types'

const props = withDefaults(
  defineProps<{
    descriptor: EventModelDescriptor | null
    /** Slice names to collapse to a placeholder column. */
    /** Keyed by slice NAME (`EventModelSliceDescriptor.name`), not by any synthetic id. */
    collapsedSlices?: ReadonlySet<string>
    /** Outcome per spec identity, from run evidence (issue #107). Colours the slice header. */
    sliceOutcomes?: Record<string, 'passed' | 'failed' | 'notRun'>
    layout?: LayoutOptions
  }>(),
  { descriptor: null, collapsedSlices: undefined, sliceOutcomes: undefined, layout: undefined }
)

const emit = defineEmits<{
  'element-click': [element: EventModelElement]
  /** The slice header was clicked — the drill-down hook (issue #108). */
  'slice-click': [slice: EventModelSliceDescriptor]
}>()

const graph = computed(() =>
  layoutEventModel(props.descriptor, {
    ...props.layout,
    collapsedSlices: props.collapsedSlices
  })
)

const isEmpty = computed(() => graph.value.nodes.length === 0)

// ------------------------------------------------------------------ zoom & pan (bobcat#182)
//
// Stoat is 36 slices and CritterWatch's fleet-wide merged model rendered 121; a canvas that can
// only be read at 100% through a horizontal scrollbar is a canvas nobody reads. Zoom is a CSS
// transform on a wrapper, deliberately NOT a scale factor threaded into `layoutEventModel`:
// layout is a pure function of the descriptor, and letting the viewport change it would put the
// two viewers' agreement at the mercy of how wide someone's window happens to be.

/** Stops, not a continuous ramp — a reader wants to be able to return to a zoom they had. */
const ZOOM_STEPS = [0.25, 0.4, 0.55, 0.7, 0.85, 1, 1.25, 1.5, 2] as const
const MIN_ZOOM = ZOOM_STEPS[0]
const MAX_ZOOM = ZOOM_STEPS[ZOOM_STEPS.length - 1]

const zoom = ref<number>(1)
const viewport = ref<HTMLElement | null>(null)
const panning = ref(false)

const canvas = computed(() => canvasSize(graph.value))
/** The wrapper's own box: a transform does not change layout size, so the scroller needs this. */
const scaledSize = computed(() => ({
  width: Math.round(canvas.value.width * zoom.value),
  height: Math.round(canvas.value.height * zoom.value)
}))

/**
 * Change the zoom and keep the point in the middle of the viewport where it was.
 *
 * `transform-origin` is the top-left, so a bare zoom change anchors there — on a canvas 41,000px
 * wide (CritterWatch's merged model) zooming in while reading slice 80 throws the reader back to
 * slice 1. What someone means by "closer" is closer to *what they are looking at*, so the scroll
 * offset moves with the scale.
 */
function applyZoom(next: number) {
  const element = viewport.value
  const previous = zoom.value
  if (!element) {
    zoom.value = next
    return
  }

  // Where the middle of the viewport sits in unscaled canvas coordinates.
  const centreX = (element.scrollLeft + element.clientWidth / 2) / previous
  const centreY = (element.scrollTop + element.clientHeight / 2) / previous

  zoom.value = next
  // After the wrapper has been re-sized, or the scroller clamps the offset to the old width.
  void nextTick(() => {
    element.scrollLeft = Math.max(0, centreX * next - element.clientWidth / 2)
    element.scrollTop = Math.max(0, centreY * next - element.clientHeight / 2)
  })
}

function zoomIn() {
  applyZoom(ZOOM_STEPS.find((step) => step > zoom.value + 0.001) ?? MAX_ZOOM)
}

function zoomOut() {
  applyZoom([...ZOOM_STEPS].reverse().find((step) => step < zoom.value - 0.001) ?? MIN_ZOOM)
}

function resetZoom() {
  applyZoom(1)
}

/**
 * Fit the canvas to the viewport's width — the one zoom that is not a step, because "all of it on
 * screen" is a measurement rather than a preference. Never zooms *in* to fill: a small model blown
 * up to 200% looks like a mistake rather than like a fit.
 */
function fitToWidth() {
  const available = viewport.value?.clientWidth ?? 0
  const width = canvas.value.width
  if (available <= 0 || width <= 0) return
  applyZoom(Math.max(MIN_ZOOM, Math.min(1, available / width)))
}

let panFrom = { x: 0, y: 0, left: 0, top: 0 }

function startPan(event: MouseEvent) {
  // Left button only, and never a drag that begins on something clickable — a slice header and a
  // card are the two things on this canvas a reader actually presses.
  if (event.button !== 0) return
  if ((event.target as HTMLElement | null)?.closest('button')) return
  const element = viewport.value
  if (!element) return

  panning.value = true
  panFrom = { x: event.clientX, y: event.clientY, left: element.scrollLeft, top: element.scrollTop }
  window.addEventListener('mousemove', onPan)
  window.addEventListener('mouseup', endPan)
}

function onPan(event: MouseEvent) {
  const element = viewport.value
  if (!element || !panning.value) return
  element.scrollLeft = panFrom.left - (event.clientX - panFrom.x)
  element.scrollTop = panFrom.top - (event.clientY - panFrom.y)
}

function endPan() {
  panning.value = false
  window.removeEventListener('mousemove', onPan)
  window.removeEventListener('mouseup', endPan)
}

// A drag that outlived the component would keep scrolling a detached element for ever.
onBeforeUnmount(endPan)

/** Ctrl/⌘ + wheel is the pinch gesture a trackpad sends; a plain wheel stays scrolling. */
function onWheel(event: WheelEvent) {
  if (!event.ctrlKey && !event.metaKey) return
  event.preventDefault()
  if (event.deltaY < 0) zoomIn()
  else zoomOut()
}

function styleFor(element: EventModelElement) {
  const fill = colorFor(element.kind)
  const outlined = OUTLINED_KINDS.includes(element.kind)
  const dashed = DASHED_KINDS.includes(element.kind)
  return {
    background: outlined ? 'transparent' : fill,
    color: outlined ? fill : inkFor(element.kind),
    border: `${outlined || dashed ? 2 : 1}px ${dashed ? 'dashed' : 'solid'} ${fill}`
  }
}

/**
 * jasperfx#704 — the hotspot behind a Hotspot card, looked up by label.
 *
 * The producer projects each hotspot into an element with `ForLabel(name, Hotspot, hotspot.Text)`,
 * so the label IS the hotspot text and that is the only join available. Worth it: without it a
 * source disagreement renders as a generic magenta sticky indistinguishable from a pending spec,
 * and "the code says this slice emits X; production says X and Y" is the most valuable thing a
 * four-source model produces.
 */
function hotspotFor(node: { element: EventModelElement; sliceName: string }): HotspotDescriptor | null {
  if (node.element.kind !== 'Hotspot') return null
  const slice = graph.value.slices.find((s) => s.name === node.sliceName)
  return (slice?.descriptor.hotspots ?? []).find((h) => h.text === node.element.label) ?? null
}

/**
 * Tooltip text. The type's full name stays the headline where there is one; the ladder rung and a
 * disagreement's two claims are appended, because both are exactly the kind of thing you want on
 * hover rather than crowding the card.
 */
function titleFor(node: { element: EventModelElement; sliceName: string }): string {
  const parts: string[] = [node.element.type?.fullName ?? node.element.label]

  const provenance = node.element.provenance
  if (provenance) parts.push(PROVENANCE_LABEL[provenance] ?? provenance)

  const hotspot = hotspotFor(node)
  if (hotspot?.origin === 'SourceDisagreement' && hotspot.winningClaim && hotspot.losingClaim) {
    parts.push(
      `Kept: ${hotspot.winningClaim.provenance} claims ${hotspot.winningClaim.value}`,
      `Dropped: ${hotspot.losingClaim.provenance} claims ${hotspot.losingClaim.value}`
    )
  }

  return parts.join('\n')
}

/**
 * bobcat#181 — an edge as an SVG polyline. The points are `layout.ts`'s routing decision, not this
 * component's: two viewers drawing the same descriptor differently is what this package exists to
 * prevent, and a route is as much a rendering claim as a coordinate.
 */
function pointsFor(edge: { points: ReadonlyArray<{ x: number; y: number }> }): string {
  return edge.points.map((p) => `${p.x},${p.y}`).join(' ')
}

/**
 * bobcat#180 — the label, split at the points a reader would break it themselves.
 *
 * Rendered with a `<wbr>` between segments because CSS offers a browser break opportunities at
 * spaces and hyphens and nowhere else: `DepositMoneyIntoAccount` and `POST /accounts/{id}/deposit`
 * are each one unbreakable word to the layout engine, which is exactly why the old fixed-width
 * card clipped them.
 */
function segmentsFor(element: EventModelElement): string[] {
  return segmentLabel(element.label ?? '')
}

/**
 * bobcat#178 — a source disagreement is a FINDING, and the card has to say so.
 *
 * The producer makes the hotspot's text the element label, so the card used to lead with the role
 * name and a clipped sentence: *"EmittedEvents: Derived claims ClaimResult; Declared claims
 * NodeClaimed, Cl…"*. The reviewer who designed the feature read it as a malformed events list,
 * which is the whole finding — nothing on the card said two sources disagreed, and the one thing
 * a four-source model produces that nothing else can deserves better than tooltip-only.
 *
 * So a hotspot card renders structure instead of prose: what kind of finding it is, then (for a
 * disagreement) the role and the two claims with their rungs, kept first. The claims are typed on
 * the descriptor — `role`, `winningClaim`, `losingClaim` — so none of this is parsed back out of
 * the sentence.
 */
const HOTSPOT_ORIGIN_LABEL: Record<string, string> = {
  SourceDisagreement: 'Sources disagree',
  PendingSpecification: 'Pending spec',
  Prose: 'Note'
}

function originLabelFor(hotspot: HotspotDescriptor): string {
  return HOTSPOT_ORIGIN_LABEL[hotspot.origin] ?? hotspot.origin
}

/** The two claims of a disagreement, kept first — null unless both are on the descriptor. */
function claimsFor(hotspot: HotspotDescriptor) {
  if (hotspot.origin !== 'SourceDisagreement') return null
  const kept = hotspot.winningClaim
  const dropped = hotspot.losingClaim
  // A disagreement whose pair did not survive the wire degrades to its text, never to half a
  // finding with one side missing.
  return kept && dropped ? { kept, dropped } : null
}

/** A trigger card whose label is an HTTP route renders its verb as a badge (#184). */
function routeFor(element: EventModelElement) {
  return element.kind === 'Trigger' ? parseRoute(element.label ?? '') : null
}

/**
 * A slice *named* for its route gets the same treatment (#184 follow-on).
 *
 * Wolverine names a query slice for its verb and route — `GET /api/clients/{id}/accounts` — so the
 * header spends its scarcest width on a word the glyph beside it has already said. Badging the verb
 * costs the header three characters instead of eight and makes the path start where the eye
 * expects it. The name itself is untouched; this is only how it is drawn.
 */
function sliceRouteFor(slice: EventModelSliceDescriptor) {
  return parseRoute(slice.name)
}

/**
 * bobcat#184 — the slice's trigger kind, as a glyph and a tooltip.
 *
 * `triggerKind` is on every derived slice and was rendered nowhere. The tooltip carries
 * `triggerOrigin` with it, which is where the route lives now that wolverine#4181 has stopped the
 * HTTP source claiming `TriggerLabel` — so the human label stays on the card and the machine
 * detail is one hover away instead of eating a column of width.
 */
function triggerIconFor(slice: EventModelSliceDescriptor): string | null {
  const kind = slice.triggerKind
  return kind ? (TRIGGER_ICON[kind] ?? null) : null
}

function triggerTitleFor(slice: EventModelSliceDescriptor): string {
  const kind = slice.triggerKind
  const parts = [kind ? (TRIGGER_KIND_LABEL[kind] ?? kind) : 'Trigger']
  if (slice.triggerOrigin) parts.push(slice.triggerOrigin)
  return parts.join(' · ')
}

/**
 * bobcat#183 — how many specifications are bound to this slice, as the header's badge.
 *
 * The count is the point, not decoration: a slice with no spec is the drift case the canvas
 * already colours orange, and until now the only way to learn a slice HAD specs was to click it.
 * Where the host supplied run evidence the badge carries the verdict too (`outcomeFor`), so a
 * failing slice says so at the same glance.
 */
function specCountFor(slice: EventModelSliceDescriptor): number {
  return (slice.specifications ?? []).length
}

function specLabelFor(slice: EventModelSliceDescriptor): string {
  const count = specCountFor(slice)
  if (count === 0) return 'no spec'
  return count === 1 ? '1 spec' : `${count} specs`
}

/** The bound identities, on hover — the drill-down is a click away, the list should not be. */
function specTitleFor(slice: EventModelSliceDescriptor): string {
  const specs = slice.specifications ?? []
  if (specs.length === 0) return 'No specification is bound to this slice'
  return specs.map((spec) => spec.identity).join('\n')
}

/** The slice's outcome, if run evidence named any of its specifications. */
function outcomeFor(sliceName: string): string | null {
  const outcomes = props.sliceOutcomes
  if (!outcomes) return null
  const slice = graph.value.slices.find((s) => s.name === sliceName)
  const identities = (slice?.descriptor.specifications ?? []).map((s) => s.identity)
  if (identities.some((id) => outcomes[id] === 'failed')) return 'failed'
  if (identities.some((id) => outcomes[id] === 'notRun')) return 'notRun'
  if (identities.length > 0 && identities.every((id) => outcomes[id] === 'passed')) return 'passed'
  return null
}
</script>

<template>
  <div class="em-canvas" data-testid="event-model">
    <p v-if="isEmpty" class="em-empty" data-testid="event-model-empty">
      No slices to render.
    </p>
    <template v-else>
      <!-- bobcat#182 — 36 slices do not fit at 100%, and 121 (CritterWatch's merged fleet model)
           are not close. Stops rather than a continuous ramp, plus a measured fit-to-width. -->
      <div class="em-toolbar">
        <button
          type="button"
          class="em-zoom em-zoom-out"
          title="Zoom out"
          :disabled="zoom <= MIN_ZOOM"
          @click="zoomOut"
        >
          −
        </button>
        <button type="button" class="em-zoom em-zoom-level" title="Reset to 100%" @click="resetZoom">
          {{ Math.round(zoom * 100) }}%
        </button>
        <button
          type="button"
          class="em-zoom em-zoom-in"
          title="Zoom in"
          :disabled="zoom >= MAX_ZOOM"
          @click="zoomIn"
        >
          +
        </button>
        <button type="button" class="em-zoom em-zoom-fit" title="Fit to width" @click="fitToWidth">
          Fit
        </button>
      </div>

      <div
        ref="viewport"
        class="em-viewport"
        :data-panning="panning ? 'true' : undefined"
        @mousedown="startPan"
        @wheel="onWheel"
      >
        <div
          class="em-zoomed"
          :style="{
            width: `${scaledSize.width}px`,
            height: `${scaledSize.height}px`,
            transform: `scale(${zoom})`
          }"
        >
          <div class="em-scroll" :style="{ gap: `${GUTTER_GAP}px`, padding: `${CANVAS_PADDING}px` }">
          <div class="em-lane-gutter" :style="{ flexBasis: `${GUTTER_WIDTH}px` }">
            <div
              v-for="lane in graph.lanes"
              :key="lane.lane"
              class="em-lane-label"
              :style="{ top: `${lane.y}px`, height: `${lane.height}px` }"
            >
              {{ LANE_LABEL[lane.lane] }}
            </div>
          </div>

          <div class="em-plot" :style="{ width: `${graph.width}px`, height: `${graph.height}px` }">
            <div
              v-for="lane in graph.lanes"
              :key="`band-${lane.lane}`"
              class="em-lane-band"
              :style="{ top: `${lane.y}px`, height: `${lane.height}px`, width: `${graph.width}px` }"
            />

            <div
              v-for="slice in graph.slices"
              :key="`slice-${slice.name}`"
              class="em-slice"
              :data-slice="slice.name"
              :data-outcome="outcomeFor(slice.name) ?? undefined"
              :style="{ left: `${slice.x}px`, width: `${slice.width}px`, height: `${graph.height}px` }"
            >
              <div class="em-slice-header" :style="{ maxWidth: `${slice.width - 8}px` }">
                <!-- bobcat#184 — what kind of thing triggers this slice, legible without reading. -->
                <svg
                  v-if="triggerIconFor(slice.descriptor)"
                  class="em-trigger-icon"
                  :data-kind="slice.descriptor.triggerKind"
                  viewBox="0 0 16 16"
                  role="img"
                  :aria-label="triggerTitleFor(slice.descriptor)"
                >
                  <title>{{ triggerTitleFor(slice.descriptor) }}</title>
                  <path :d="triggerIconFor(slice.descriptor) ?? undefined" />
                </svg>
                <button
                  class="em-slice-name"
                  type="button"
                  :title="slice.name"
                  @click="emit('slice-click', slice.descriptor)"
                >
                  <template v-if="sliceRouteFor(slice.descriptor)"
                    ><span class="em-route-method">{{
                      sliceRouteFor(slice.descriptor)!.method
                    }}</span
                    >{{ sliceRouteFor(slice.descriptor)!.path }}</template
                  >
                  <template v-else>{{ slice.name }}</template>
                </button>
                <!-- bobcat#183 — the bound-specification count, verdict-tinted where the host gave
                     run evidence. `no spec` is deliberately spelled out rather than shown as 0: it is
                     the drift case, and it should read as a finding. -->
                <!-- A button, not a label: it names the specifications, so a reader clicks it
                     expecting to see them. It opens the drawer the slice name opens. -->
                <button
                  class="em-slice-specs"
                  type="button"
                  :data-outcome="outcomeFor(slice.name) ?? (specCountFor(slice.descriptor) === 0 ? 'none' : undefined)"
                  :data-count="specCountFor(slice.descriptor)"
                  :title="specTitleFor(slice.descriptor)"
                  @click="emit('slice-click', slice.descriptor)"
                >
                  {{ specLabelFor(slice.descriptor) }}
                </button>
              </div>
            </div>

            <!-- bobcat#181 — the edges the descriptor already computes, which the canvas used to drop
                 on the floor. Behind the cards in DOM order and pointer-inert, so a line never eats a
                 card's click; `currentColor` so it inherits the host's ink in either theme. -->
            <svg
              class="em-edges"
              :width="graph.width"
              :height="graph.height"
              :viewBox="`0 0 ${graph.width} ${graph.height}`"
              aria-hidden="true"
            >
              <defs>
                <marker
                  id="em-arrow"
                  markerWidth="6"
                  markerHeight="6"
                  refX="5"
                  refY="3"
                  orient="auto"
                  markerUnits="strokeWidth"
                >
                  <path d="M0,0 L6,3 L0,6 z" fill="currentColor" />
                </marker>
              </defs>
              <polyline
                v-for="edge in graph.edges"
                :key="`${edge.fromId}->${edge.toId}`"
                class="em-edge"
                :data-from="edge.fromId"
                :data-to="edge.toId"
                :points="pointsFor(edge)"
                marker-end="url(#em-arrow)"
              />
            </svg>

            <button
              v-for="node in graph.nodes"
              :key="node.id"
              class="em-card"
              type="button"
              :data-kind="node.element.kind"
              :data-lane="node.element.lane"
              :data-provenance="node.element.provenance ?? undefined"
              :data-hotspot-origin="hotspotFor(node)?.origin ?? undefined"
              :title="titleFor(node)"
              :style="{
                left: `${node.x}px`,
                top: `${node.y}px`,
                width: `${node.width}px`,
                height: `${node.height}px`,
                padding: `6px ${CARD_PADDING_X}px`,
                fontSize: `${LABEL_FONT_SIZE}px`,
                lineHeight: `${LABEL_LINE_HEIGHT}`,
                '--em-label-lines': MAX_LABEL_LINES,
                ...styleFor(node.element)
              }"
              @click="emit('element-click', node.element)"
            >
              <span v-if="hotspotFor(node)" class="em-hotspot">
                <span class="em-hotspot-origin">{{ originLabelFor(hotspotFor(node)!) }}</span>
                <template v-if="claimsFor(hotspotFor(node)!)">
                  <span v-if="hotspotFor(node)!.role" class="em-hotspot-role">{{
                    hotspotFor(node)!.role
                  }}</span>
                  <span class="em-hotspot-claim" data-claim="kept">
                    <span class="em-hotspot-rung">{{ claimsFor(hotspotFor(node)!)!.kept.provenance }}</span>
                    {{ claimsFor(hotspotFor(node)!)!.kept.value }}
                  </span>
                  <span class="em-hotspot-claim" data-claim="dropped">
                    <span class="em-hotspot-rung">{{
                      claimsFor(hotspotFor(node)!)!.dropped.provenance
                    }}</span>
                    {{ claimsFor(hotspotFor(node)!)!.dropped.value }}
                  </span>
                </template>
                <span v-else class="em-hotspot-text">{{ hotspotFor(node)!.text }}</span>
              </span>
              <span v-else-if="routeFor(node.element)" class="em-card-label"
                ><span class="em-route-method">{{ routeFor(node.element)!.method }}</span
                ><template
                  v-for="(segment, index) in segmentLabel(routeFor(node.element)!.path)"
                  :key="index"
                  >{{ segment }}<wbr /></template
              ></span>
              <span v-else class="em-card-label"
                ><template v-for="(segment, index) in segmentsFor(node.element)" :key="index"
                  >{{ segment }}<wbr /></template
              ></span>
            </button>
            </div>
          </div>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.em-canvas {
  position: relative;
  width: 100%;
}
/* The scroller, and the pan surface. Grab-to-drag because at 55% a 121-slice model is still
   several screens wide, and a horizontal scrollbar is a poor way to travel that. */
.em-viewport {
  width: 100%;
  overflow: auto;
  cursor: grab;
}
.em-viewport[data-panning='true'] {
  cursor: grabbing;
  user-select: none;
}
/* Scaled from its top-left, with its own box set to the scaled size — a transform does not change
   layout size, so without the explicit width/height the scroller would still think the canvas was
   its 100% self. */
.em-zoomed {
  transform-origin: 0 0;
}
.em-toolbar {
  display: flex;
  justify-content: flex-end;
  gap: 4px;
  padding: 4px 12px 0;
}
.em-zoom {
  min-width: 26px;
  padding: 1px 6px;
  border: 1px solid currentColor;
  border-radius: 4px;
  background: transparent;
  color: inherit;
  font: inherit;
  font-size: 11px;
  line-height: 16px;
  opacity: 0.55;
  cursor: pointer;
}
.em-zoom:hover:not(:disabled) {
  opacity: 1;
}
.em-zoom:disabled {
  opacity: 0.25;
  cursor: default;
}
.em-zoom-level {
  min-width: 44px;
}
/* Gap and padding are bound from layout.ts's canvas constants, which is what canvasSize measures
   the zoom wrapper with — the same anti-drift rule as the card's type scale. */
.em-scroll {
  display: flex;
}
.em-lane-gutter {
  position: relative;
  flex-grow: 0;
  flex-shrink: 0;
}
.em-lane-label {
  position: absolute;
  display: flex;
  align-items: center;
  font-size: 12px;
  font-weight: 600;
  opacity: 0.7;
}
.em-plot {
  position: relative;
  flex: 0 0 auto;
}
.em-lane-band {
  position: absolute;
  left: 0;
  border-top: 1px solid currentColor;
  opacity: 0.12;
}
.em-slice {
  position: absolute;
  top: 0;
  border-left: 1px dashed currentColor;
  opacity: 0.45;
  pointer-events: none;
}
.em-slice[data-outcome='failed'] {
  border-left-color: #e5484d;
  opacity: 1;
}
.em-slice[data-outcome='passed'] {
  border-left-color: #46a758;
  opacity: 1;
}
/* notRun IS the drift colour — a bound spec that has not run is a claim without evidence, and it
   should read that way on the canvas itself in every viewer, not only in a host's own chrome. */
.em-slice[data-outcome='notRun'] {
  border-left-color: #e8930c;
  opacity: 1;
}
/* The overlay itself stays pointer-inert so cards keep their clicks; the name is the one
   interactive part — the slice's drill-down handle. */
.em-slice-header {
  position: absolute;
  top: -2px;
  left: 4px;
  display: flex;
  align-items: center;
  gap: 6px;
}
.em-trigger-icon {
  flex: 0 0 auto;
  width: 12px;
  height: 12px;
  fill: none;
  stroke: currentColor;
  stroke-width: 1.3;
  stroke-linecap: round;
  stroke-linejoin: round;
  opacity: 0.6;
  pointer-events: auto;
}
.em-slice-name {
  /* flex-shrink with min-width: 0 is what lets the NAME give way to the badge rather than pushing
     it out of the column — the count is short and fixed, the name is not. */
  flex: 0 1 auto;
  min-width: 0;
  padding: 0;
  border: none;
  background: transparent;
  color: inherit;
  font: inherit;
  font-size: 11px;
  white-space: nowrap;
  /* A long slice name used to run across its neighbour's column. It now ends in an ellipsis
     inside its own column, with the full name on the title (#180). */
  overflow: hidden;
  text-overflow: ellipsis;
  cursor: pointer;
  pointer-events: auto;
}
.em-slice-specs {
  flex: 0 0 auto;
  padding: 0 5px;
  background: transparent;
  color: inherit;
  font: inherit;
  cursor: pointer;
  border: 1px solid currentColor;
  border-radius: 8px;
  font-size: 10px;
  line-height: 15px;
  opacity: 0.75;
  white-space: nowrap;
  pointer-events: auto;
}
.em-slice-specs[data-outcome='passed'] {
  color: #46a758;
  opacity: 1;
}
.em-slice-specs[data-outcome='failed'] {
  color: #e5484d;
  opacity: 1;
}
/* notRun and none share the drift colour on purpose: a claim with no evidence and a claim with
   nothing to produce evidence are the same problem at different stages. */
.em-slice-specs[data-outcome='notRun'],
.em-slice-specs[data-outcome='none'] {
  color: #e8930c;
  opacity: 1;
}
/* jasperfx#703 — the ladder, as a SECOND channel. Fill colour is spoken for: it means the element
   KIND, and that agreement between viewers is the whole reason this package exists. So provenance
   rides a corner marker instead, and only the top rung gets one.

   Declared and Derived are deliberately left looking exactly as they did. An unattributed model
   reads Declared rather than absent, so fading it would fade most of a typical canvas — and the
   new information here is "production has SEEN this", not "this was only written down". */
.em-card[data-provenance='Observed']::after {
  content: '';
  position: absolute;
  top: 0;
  right: 0;
  border-width: 0 10px 10px 0;
  border-style: solid;
  border-color: transparent currentColor transparent transparent;
  opacity: 0.85;
}

/* jasperfx#704 — a source disagreement is a FINDING, not decoration, and it is worth more than the
   generic magenta sticky every other hotspot gets. A double outline in the hotspot colour reads as
   "two sources, one of them dropped" at a glance, and the two claims are on the tooltip. */
.em-card[data-hotspot-origin='SourceDisagreement'] {
  outline: 2px solid #e91e63;
  outline-offset: 2px;
  font-weight: 600;
}

/* The edge layer fills the plot and never intercepts a pointer — the cards are the interactive
   things, and a line crossing one must not steal its click. */
.em-edges {
  position: absolute;
  top: 0;
  left: 0;
  overflow: visible;
  pointer-events: none;
}
.em-edge {
  fill: none;
  stroke: currentColor;
  stroke-width: 1.5;
  stroke-linejoin: round;
  opacity: 0.45;
}

.em-card {
  position: absolute;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 4px;
  font: inherit;
  text-align: center;
  cursor: pointer;
  overflow: hidden;
  /* padding, font-size and line-height come from layout.ts as inline style: the column width was
     computed from that type scale, and a stylesheet holding a second opinion about it is a
     clipped label with no traceable symptom (#180). */
}
/* Wrap at the `<wbr>` opportunities, then clamp — a name too long even for the widened column
   ends in an ellipsis with its full text on the card's tooltip, rather than being cut mid-glyph
   by `overflow: hidden` as it was before #180. `anywhere` is the backstop for the one segment
   that is itself wider than the cap. */
/* bobcat#178 — a hotspot card is a finding, laid out as one: what kind, then (for a source
   disagreement) the role and the two claims with their rungs, kept above dropped. Left-aligned
   and stacked, because a centred sentence is what made it read as a malformed events list. */
.em-hotspot {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 1px;
  width: 100%;
  text-align: left;
  overflow: hidden;
}
.em-hotspot-origin {
  font-size: 9px;
  font-weight: 700;
  letter-spacing: 0.6px;
  text-transform: uppercase;
  opacity: 0.9;
}
.em-hotspot-role {
  font-size: 11px;
  font-weight: 600;
}
.em-hotspot-claim {
  /* stretch + min-width: 0 is what actually makes the ellipsis appear: a column flex item is
     shrink-to-fit by default, so a long claim sizes to its text and gets cut by the card's
     overflow with no ellipsis to say it had been. */
  align-self: stretch;
  min-width: 0;
  font-size: 10px;
  line-height: 1.3;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
/* The dropped claim is stated, not hidden — it is half the finding — but it reads as the loser:
   struck through and faded, where the kept one carries full weight. */
.em-hotspot-claim[data-claim='dropped'] {
  opacity: 0.7;
  text-decoration: line-through;
}
.em-hotspot-rung {
  font-weight: 700;
}
.em-hotspot-rung::after {
  content: ' ·';
}
.em-hotspot-text {
  font-size: 11px;
  line-height: 1.25;
  overflow-wrap: anywhere;
  display: -webkit-box;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 3;
  line-clamp: 3;
  overflow: hidden;
}

/* The verb of a route, as a badge: three to seven characters of fixed vocabulary a reader
   recognises by shape, so it should not be competing with the path for the same line (#184). */
.em-route-method {
  display: inline-block;
  margin-right: 4px;
  padding: 0 4px;
  border-radius: 3px;
  border: 1px solid currentColor;
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 0.3px;
  /* Outlined in the card's own ink rather than filled: the fill means the element KIND, and a
     badge that painted over it would be spending the one channel this package guards. */
  opacity: 0.75;
}
.em-card-label {
  display: -webkit-box;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: var(--em-label-lines, 3);
  line-clamp: var(--em-label-lines, 3);
  overflow: hidden;
  overflow-wrap: anywhere;
}
.em-empty {
  padding: 24px;
  opacity: 0.6;
}
</style>
