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
import { computed } from 'vue'
import {
  CARD_PADDING_X,
  LABEL_FONT_SIZE,
  LABEL_LINE_HEIGHT,
  MAX_LABEL_LINES,
  layoutEventModel,
  type LayoutOptions
} from './layout'
import { segmentLabel } from './text'
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
    <div v-else class="em-scroll">
      <div class="em-lane-gutter">
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
          <button
            class="em-slice-name"
            type="button"
            :style="{ maxWidth: `${slice.width - 8}px` }"
            :title="slice.name"
            @click="emit('slice-click', slice.descriptor)"
          >
            {{ slice.name }}
          </button>
        </div>

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
          <span class="em-card-label"
            ><template v-for="(segment, index) in segmentsFor(node.element)" :key="index"
              >{{ segment }}<wbr /></template
          ></span>
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.em-canvas {
  position: relative;
  width: 100%;
  overflow: auto;
}
.em-scroll {
  display: flex;
  gap: 12px;
  padding: 12px;
}
.em-lane-gutter {
  position: relative;
  flex: 0 0 132px;
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
.em-slice-name {
  position: absolute;
  top: -2px;
  left: 4px;
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
