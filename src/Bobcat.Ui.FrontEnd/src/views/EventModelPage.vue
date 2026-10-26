<script setup lang="ts">
/**
 * The Event Model page (issue #108): renders the pushed descriptor through the shared
 * @jasperfx/event-model-vue renderer — the same component CritterWatch consumes, which is what
 * makes "the same descriptor renders identically in both viewers" true by construction —
 * colours slices from a run's evidence (issue #107), and drills down from a clicked slice to
 * its bound scenarios — each rendered as a whole specification through the same component the run
 * page uses, and linked to its own page (stoat#61).
 *
 * bobcat#799: a console holds several models. The header names the one on screen and says when each of
 * its sources was last pushed and from where, and a switcher picks another, kept in the URL as
 * `?model=` so a reload or a pasted link lands on the same one. With no `?model=` the page opens on the
 * console's preferred model: the newest pushed from a repository with runs on the board, else the
 * newest of all. The renderer is the shared package's and knows nothing of this; it is all page.
 */
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
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
import {
  outcomesFor,
  patternLabel,
  patternViolationsOf,
  sliceFor,
  sourceOrigin,
  undeclaredTouches,
  useEventModelStore,
  type EventModelSourceSummary
} from '@/stores/event-model-store'
import { useRunsStore, type RunState, type ScenarioState } from '@/stores/runs-store'
import SpecificationView from '@/components/SpecificationView.vue'
import RunButton from '@/components/RunButton.vue'
import { specHref } from '@/lib/specLinks'
import { formatAbsolute, formatRelative, useNow } from '@/composables/time'

const model = useEventModelStore()
const runs = useRunsStore()
const route = useRoute()
const router = useRouter()

/** A single string from a query value, or null — a hand-edited URL can hold an array or nothing. */
function queryString(name: string): string | null {
  const value = route?.query?.[name]
  const first = Array.isArray(value) ? value[0] : value
  return typeof first === 'string' && first.length > 0 ? first : null
}

onMounted(async () => {
  // The model to show: the URL's, else the console's preferred one (bobcat#799). A store another
  // page already loaded with the same selection is left as it is, rather than re-read for nothing.
  const listing = await model.loadModels()
  const choice = queryString('model') ?? listing?.preferred ?? null
  if (model.status === 'idle' || choice !== model.selected) await model.select(choice)
})

// ---------------------------------------------------------------- which model (bobcat#799)

const now = useNow()

/** Every model the console holds, newest push first. */
const models = computed(() => model.listing?.models ?? [])

/** The name the switcher shows: the model on screen, or the one asked for when it is not there. */
const shownName = computed(() => model.descriptor?.name ?? model.selected)

function pushedLabel(source: EventModelSourceSummary): string {
  const ago = formatRelative(source.pushedAt, now.value) ?? 'at an unknown time'
  // A half stored before the console kept push times has its file's write time instead.
  return `${source.pushedAtIsFileTime ? 'last written' : 'last pushed'} ${ago}`
}

function switchModel(name: string) {
  if (name === shownName.value) return

  // The viewport, the selection and an arrived-for spec all belong to the model being left.
  const query: Record<string, unknown> = { ...route.query, model: name }
  for (const key of ['z', 'x', 'y', 'focus', 'sel', 'spec']) delete query[key]
  drilled.value = null
  arrivedFor.value = null

  void router.replace({ query: query as typeof route.query }).catch(() => {})
  void model.select(name)
}

// Evidence source: newest run first, user-switchable. The join is by spec identity, so any
// run of the bound suite colours the canvas; an empty box means design-time only.
// A link from a run (stoat#61) names it with `?run=`, so the canvas is coloured from the run the
// reader came from rather than whichever happens to be newest.
const selectedRunId = ref<string | null>(queryString('run'))
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

/**
 * The specification a link arrived for (stoat#61): `?spec={Feature}/{Scenario}` opens the drill-down
 * on the slice that binds it and marks that spec. The model may not be loaded yet on a hard
 * navigation, so this waits for it rather than reading once.
 */
const arrivedFor = ref<string | null>(queryString('spec'))

watch(
  () => model.descriptor,
  (descriptor) => {
    if (!arrivedFor.value || drilled.value) return
    const owner = sliceFor(descriptor, arrivedFor.value)
    if (owner) drilled.value = owner
  },
  { immediate: true }
)

function closeDrill() {
  drilled.value = null
  if (!arrivedFor.value) return
  arrivedFor.value = null
  // The spec the reader arrived for is spent once the drawer closes; leaving it in the URL would
  // reopen the drawer on the next reload of a canvas the reader has moved on from.
  if (router && route) {
    const query = { ...route.query }
    delete query.spec
    void router.replace({ query }).catch(() => {})
  }
}

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
  // bobcat#545: an Acceptance spec not yet passing is unfinished work, not a red build.
  if (scenario.outcome === 'AcceptanceFailing') return 'warning'
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
      <h2>Event Model<span v-if="model.descriptor" data-testid="model-name"> — {{ model.descriptor.name }}</span></h2>
      <el-select
        v-if="models.length > 1"
        :model-value="shownName"
        size="small"
        class="bm-em-model-select"
        data-testid="model-switcher"
        aria-label="Event Model"
        @update:model-value="switchModel"
      >
        <template #prefix>model</template>
        <el-option
          v-for="entry in models"
          :key="entry.name"
          :value="entry.name"
          :label="entry.name"
          :data-model="entry.name"
        >
          <span>{{ entry.name }}</span>
          <span class="bm-em-model-option-age">{{ formatRelative(entry.lastPushedAt, now) }}</span>
        </el-option>
      </el-select>
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

    <!-- bobcat#799: when each source was last pushed, and from where, so a stale model says so. -->
    <div v-if="model.shownModel" class="bm-em-provenance" data-testid="model-provenance">
      <span
        v-for="source in model.shownModel.sources"
        :key="source.source"
        class="bm-em-source"
        data-testid="model-source"
        :data-source="source.source"
      >
        <code>{{ source.source }}</code>
        <span :title="formatAbsolute(source.pushedAt) ?? undefined">{{ pushedLabel(source) }}</span>
        <template v-if="sourceOrigin(source)"> from <strong>{{ sourceOrigin(source) }}</strong></template>
      </span>
      <el-tag
        v-if="!model.shownModel.hasRunsOnBoard && models.some((m) => m.hasRunsOnBoard)"
        size="small"
        type="warning"
        data-testid="model-not-on-board"
      >
        no run on the board is from this model's repository
      </el-tag>
    </div>

    <el-empty
      v-if="model.status === 'absent' && model.selected && models.length > 0"
      data-testid="event-model-unknown"
      :description="`This console holds no Event Model named '${model.selected}'.`"
    />

    <el-empty
      v-else-if="model.status === 'absent'"
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

    <!-- Keyed by name, so switching models starts the canvas afresh rather than at the last one's viewport. -->
    <EventModelView
      v-else-if="model.descriptor"
      :key="model.descriptor.name"
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
      @close="closeDrill"
    >
      <div v-if="drilled" data-testid="slice-drilldown">
        <!-- jasperfx#1003: a Query slice is a read in its own right, and says what it may not do. -->
        <div class="bm-em-pattern">
          <el-tag size="small" type="info" :data-pattern="drilled.pattern ?? undefined" data-testid="slice-pattern">
            {{ patternLabel(drilled.pattern) }}
          </el-tag>
        </div>
        <el-alert
          v-for="violation in patternViolationsOf(drilled)"
          :key="`${violation.role}:${violation.text}`"
          type="warning"
          :closable="false"
          :title="violation.text"
          class="bm-em-violation"
          data-testid="pattern-violation"
        />
        <!-- stoat#78, the natural demo: every spec bound to the slice, on a resident runner. -->
        <div v-if="boundSpecs.length > 0" class="bm-em-run">
          <RunButton :target="{ specs: boundSpecs.map((b) => b.identity) }" label="Run this slice" />
        </div>
        <p v-if="boundSpecs.length === 0" class="bm-em-nospec" data-testid="no-specs">
          No specification is bound to this slice — the orange of drift colouring.
        </p>

        <div
          v-for="bound in boundSpecs"
          :key="bound.identity"
          class="bm-em-spec"
          :data-spec="bound.identity"
          :data-arrived="bound.identity === arrivedFor ? 'true' : undefined"
        >
          <div class="bm-em-spec-title">
            <!-- Slice → specification (stoat#61): the rendered spec, on a page of its own. -->
            <router-link
              v-if="bound.scenario && evidenceRun"
              :to="specHref(evidenceRun.runId, bound.identity)"
              class="bm-em-spec-link"
              data-testid="open-spec"
            >
              {{ bound.identity }}
            </router-link>
            <template v-else>{{ bound.identity }}</template>
            <el-tag size="small" :type="verdictType(bound.scenario)" data-testid="spec-verdict">
              {{ verdictLabel(bound.scenario) }}
            </el-tag>
          </div>

          <SpecificationView v-if="bound.scenario" :scenario="bound.scenario" />

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
.bm-em-run {
  margin-bottom: 12px;
}

.bm-em-pattern {
  margin-bottom: 12px;
}

.bm-em-violation {
  margin-bottom: 12px;
}

.bm-em-header {
  display: flex;
  align-items: center;
  gap: 16px;
}

.bm-em-run-select {
  width: 320px;
}

.bm-em-model-select {
  width: 220px;
}

.bm-em-model-option-age {
  float: right;
  margin-left: 12px;
  font-size: 12px;
  opacity: 0.65;
}

.bm-em-provenance {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px 18px;
  margin: -8px 0 12px;
  font-size: 13px;
  opacity: 0.85;
}

.bm-em-source code {
  margin-right: 4px;
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

.bm-em-spec[data-arrived='true'] {
  border-left: 3px solid var(--bm-primary);
  padding-left: 8px;
}

.bm-em-spec-link {
  color: var(--bm-link);
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
