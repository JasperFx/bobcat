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
import { layoutEventModel, type LayoutOptions } from './layout'
import { colorFor, inkFor, DASHED_KINDS, OUTLINED_KINDS } from './palette'
import {
  LANE_LABEL,
  type EventModelDescriptor,
  type EventModelElement,
  type EventModelSliceDescriptor
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
          :title="node.element.type?.fullName ?? node.element.label"
          :style="{
            left: `${node.x}px`,
            top: `${node.y}px`,
            width: `${node.width}px`,
            height: `${node.height}px`,
            ...styleFor(node.element)
          }"
          @click="emit('element-click', node.element)"
        >
          {{ node.element.label }}
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
  cursor: pointer;
  pointer-events: auto;
}
.em-card {
  position: absolute;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 6px 8px;
  border-radius: 4px;
  font: inherit;
  font-size: 13px;
  line-height: 1.25;
  text-align: center;
  cursor: pointer;
  overflow: hidden;
}
.em-empty {
  padding: 24px;
  opacity: 0.6;
}
</style>
