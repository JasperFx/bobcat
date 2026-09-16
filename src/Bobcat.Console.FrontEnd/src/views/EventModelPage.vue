<script setup lang="ts">
/**
 * The Event Model page (issue #108): renders the pushed descriptor through the shared
 * @jasperfx/event-model-vue renderer — the same component CritterWatch consumes, which is what
 * makes "the same descriptor renders identically in both viewers" true by construction —
 * colours slices from a run's evidence (issue #107), and drills down from a clicked slice to
 * its bound scenarios with their step results.
 */
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  EventModelView,
  viewportFromQuery,
  viewportToQuery,
  type EventModelElement,
  type EventModelSliceDescriptor,
  type ViewportState
} from '@jasperfx/event-model-vue'
import '@jasperfx/event-model-vue/style.css'
import { outcomesFor, undeclaredTouches, useEventModelStore } from '@/stores/event-model-store'
import { useRunsStore, type RunState, type ScenarioState } from '@/stores/runs-store'

const model = useEventModelStore()
const runs = useRunsStore()

onMounted(() => {
  if (model.status === 'idle') void model.load()
})

// Evidence source: newest run first, user-switchable. The join is by spec identity, so any
// run of the bound suite colours the canvas; an empty box means design-time only.
const selectedRunId = ref<string | null>(null)
const runChoices = computed<RunState[]>(() =>
  [...runs.allRuns].sort((a, b) => (b.startedAt ?? '').localeCompare(a.startedAt ?? ''))
)
const evidenceRun = computed<RunState | undefined>(() =>
  selectedRunId.value ? runs.runById(selectedRunId.value) : runChoices.value[0]
)

const sliceOutcomes = computed(() =>
  evidenceRun.value ? outcomesFor(model.descriptor, evidenceRun.value.scenarios) : undefined
)

// ---------------------------------------------------------------- drill-down

const drilled = ref<EventModelSliceDescriptor | null>(null)

function openSlice(slice: EventModelSliceDescriptor) {
  drilled.value = slice
}

// A click on any card drills into the slice that owns it — the element ids on the wire are
// unique per slice, so ownership is a lookup, not a parse.
function openElement(element: EventModelElement) {
  const owner = model.slices.find((s) => (s.elements ?? []).some((e) => e.id === element.id))
  if (owner) drilled.value = owner
}

interface BoundSpec {
  identity: string
  scenario: ScenarioState | undefined
  undeclared: string[]
}

const boundSpecs = computed<BoundSpec[]>(() =>
  (drilled.value?.specifications ?? []).map((spec) => {
    const scenario = evidenceRun.value?.scenarios[spec.identity]
    return {
      identity: spec.identity,
      scenario,
      undeclared: drilled.value ? undeclaredTouches(drilled.value, scenario) : []
    }
  })
)

function verdictType(scenario: ScenarioState | undefined): 'success' | 'danger' | 'warning' | 'info' {
  if (!scenario || !scenario.outcome) return 'warning' // not run (or still running) — the drift colour
  if (scenario.outcome === 'CleanPass') return 'success'
  if (scenario.outcome === 'PassOnRetry') return 'warning'
  return 'danger'
}

function verdictLabel(scenario: ScenarioState | undefined): string {
  return scenario?.outcome ?? 'not run'
}

// ---------------------------------------------------------------- viewport in the URL (#296)
//
// The renderer reports where the reader is; the PAGE decides that "where" belongs in the route
// query. That split is deliberate — a host embedding the canvas in a dashboard tile wants none of
// this, and a URL is the console's own idea of persistence — but the *encoding* comes from the
// package (`viewportToQuery`/`viewportFromQuery`) so a Bobcat link and a CritterWatch link to
// "CreditWallet, focused" mean the same thing.
//
// The payoff is the one the issue names: a link to a part of a 106-slice model can be pasted into
// a PR, and the reader lands on the same view rather than on the far left of a 10,000px canvas.

const route = useRoute()
const router = useRouter()

/**
 * Read once, on mount. Not a `watch` on the query: the canvas writes the query as the reader moves,
 * and a watch that fed it back would fight the reader for the scroll position.
 */
const initialViewport = computed(() => viewportFromQuery(route?.query as Record<string, unknown>))

/**
 * Leading-edge, then at most one write per {@link URL_COOLDOWN_MS}.
 *
 * The canvas reports every scroll frame, honestly — it does not get to decide what a host finds
 * expensive. A URL write per frame is expensive: `history.replaceState` is rate-limited by the
 * browser (Safari drops calls past ~100 in 30 seconds), and a single drag across a 106-slice model
 * is several hundred. Leading edge so a discrete action — focus, a zoom button, Esc — still lands
 * in the URL immediately; trailing so a drag ends up at the place it finished.
 */
const URL_COOLDOWN_MS = 100
let cooling: ReturnType<typeof setTimeout> | null = null
let pending: ViewportState | null = null

function writeViewport(viewport: ViewportState) {
  const query = { ...route.query, ...viewportToQuery(viewport) }
  // The package omits an absent focus/selection rather than writing empty keys, so they are cleared
  // here instead — otherwise clearing a focus would leave its crumb in the URL for ever.
  if (!viewport.focus) delete (query as Record<string, unknown>).focus
  if (!viewport.selection) delete (query as Record<string, unknown>).sel

  // `replace`, never `push`: a wheel zoom is not a navigation, and pushing one would make Back
  // walk the reader through every notch of it.
  void router.replace({ query }).catch(() => {
    // A navigation cancelled by a newer one is the normal case while someone is dragging.
  })
}

function onViewportChange(viewport: ViewportState) {
  if (!router || !route) return

  if (cooling) {
    pending = viewport
    return
  }

  writeViewport(viewport)
  cooling = setTimeout(() => {
    cooling = null
    const last = pending
    pending = null
    if (last) onViewportChange(last)
  }, URL_COOLDOWN_MS)
}

onBeforeUnmount(() => {
  if (cooling) clearTimeout(cooling)
})
</script>

<template>
  <div>
    <div class="bm-em-header">
      <h2>Event Model<span v-if="model.descriptor"> — {{ model.descriptor.name }}</span></h2>
      <el-select
        v-if="runChoices.length > 0"
        :model-value="evidenceRun?.runId ?? null"
        placeholder="run evidence"
        size="small"
        class="bm-em-run-select"
        data-testid="evidence-run"
        @update:model-value="(v: string) => (selectedRunId = v)"
      >
        <el-option
          v-for="run in runChoices"
          :key="run.runId"
          :value="run.runId"
          :label="`${run.suite} — ${new Date(run.startedAt).toLocaleString()}`"
        />
      </el-select>
    </div>

    <el-empty
      v-if="model.status === 'absent'"
      data-testid="event-model-absent"
      description="No Event Model has been published yet."
    >
      <p class="bm-em-hint">
        Push one with
        <code>curl -X PUT --data @event-model.json http://localhost:5525/api/event-model</code>
        — the file Wolverine's <code>event-model</code> export writes, or a descriptor a Bobcat
        spec assembly reported.
      </p>
    </el-empty>

    <el-alert
      v-else-if="model.status === 'error'"
      type="error"
      title="Could not load the Event Model."
      :closable="false"
    />

    <EventModelView
      v-else-if="model.descriptor"
      :descriptor="model.descriptor"
      :slice-outcomes="sliceOutcomes"
      :initial-viewport="initialViewport"
      @slice-click="openSlice"
      @element-click="openElement"
      @viewport-change="onViewportChange"
    />

    <el-drawer
      :model-value="drilled !== null"
      :title="drilled?.name"
      size="40%"
      @close="drilled = null"
    >
      <div v-if="drilled" data-testid="slice-drilldown">
        <p v-if="boundSpecs.length === 0" class="bm-em-nospec" data-testid="no-specs">
          No specification is bound to this slice — the orange of drift colouring.
        </p>

        <div
          v-for="bound in boundSpecs"
          :key="bound.identity"
          class="bm-em-spec"
          :data-spec="bound.identity"
        >
          <div class="bm-em-spec-title">
            {{ bound.identity }}
            <el-tag size="small" :type="verdictType(bound.scenario)" data-testid="spec-verdict">
              {{ verdictLabel(bound.scenario) }}
            </el-tag>
          </div>

          <ul v-if="bound.scenario" class="bm-em-steps">
            <li
              v-for="step in bound.scenario.steps"
              :key="step.stepId"
              :data-status="step.status"
              class="bm-em-step"
            >
              <span v-if="step.kind" class="bm-em-step-kind">{{ step.kind }}</span>
              {{ step.text }}
              <span v-if="step.durationMs != null" class="bm-em-step-ms">{{ step.durationMs }}ms</span>
              <div v-if="step.errorMessage" class="bm-em-step-error">{{ step.errorMessage }}</div>
            </li>
          </ul>

          <div v-if="bound.scenario && bound.scenario.touchedTypes.length > 0" class="bm-em-touched">
            <span class="bm-em-touched-label">touched:</span>
            <el-tag
              v-for="touched in bound.scenario.touchedTypes"
              :key="touched.fullName"
              size="small"
              :type="bound.undeclared.includes(touched.fullName) ? 'warning' : 'info'"
              :title="touched.fullName"
              class="bm-em-touched-tag"
            >
              {{ touched.name }}
            </el-tag>
            <span
              v-if="bound.undeclared.length > 0"
              class="bm-em-undeclared"
              data-testid="undeclared-touches"
            >
              {{ bound.undeclared.length }} touched type(s) the model does not declare
            </span>
          </div>
        </div>
      </div>
    </el-drawer>
  </div>
</template>

<style scoped>
.bm-em-header {
  display: flex;
  align-items: center;
  gap: 16px;
}

.bm-em-run-select {
  width: 320px;
}

.bm-em-hint {
  max-width: 560px;
  font-size: 13px;
  opacity: 0.8;
}

.bm-em-spec {
  margin-bottom: 20px;
}

.bm-em-spec-title {
  font-weight: 600;
  margin-bottom: 6px;
}

.bm-em-steps {
  margin: 0 0 8px;
  padding-left: 18px;
  font-size: 13px;
}

.bm-em-step[data-status='failed'],
.bm-em-step[data-status='error'] {
  color: var(--el-color-danger);
}

.bm-em-step-kind {
  font-weight: 600;
  margin-right: 4px;
}

.bm-em-step-ms {
  opacity: 0.6;
  margin-left: 6px;
  font-size: 12px;
}

.bm-em-step-error {
  font-size: 12px;
  opacity: 0.85;
}

.bm-em-touched {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  font-size: 12px;
}

.bm-em-touched-label {
  opacity: 0.7;
}

.bm-em-undeclared {
  color: var(--el-color-warning);
}

.bm-em-nospec {
  opacity: 0.75;
}
</style>
