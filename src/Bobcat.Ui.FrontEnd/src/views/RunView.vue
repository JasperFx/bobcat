<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import ScenarioProgress from '@/components/ScenarioProgress.vue'
import SpecificationView from '@/components/SpecificationView.vue'
import { useRunsStore, visibleScenarios, type ScenarioState } from '@/stores/runs-store'
import SupervisorTopology from '@/components/SupervisorTopology.vue'
import RunButton from '@/components/RunButton.vue'
import MissingRun from '@/components/MissingRun.vue'
import Breadcrumbs from '@/components/Breadcrumbs.vue'
import LiveRunProgress from '@/components/LiveRunProgress.vue'
import RunToc from '@/components/RunToc.vue'
import { useRunners } from '@/composables/useRunners'
import { formatAbsolute, formatDuration, formatRelative, useNow } from '@/composables/time'
import { ensureRunEvents } from '@/messages/hydrate'
import { commandForRun, describeCommand, rebuiltSince } from '@/lib/runners'
import { commandLineOf, plannedHintOf } from '@/lib/liveProgress'
import { sliceFor, useEventModelStore } from '@/stores/event-model-store'
import { sliceHref, specHref } from '@/lib/specLinks'
import { runCrumb, runsCrumb, suiteCrumb } from '@/lib/crumbs'
import {
  buildRunTree,
  runningScenario,
  scenarioAnchor,
  startsExpanded,
  summarize,
  type TocStatus,
} from '@/lib/runTree'

/**
 * One run (bobcat#787): its identity in the header, the summary tiles and the overall bar, a
 * table-of-contents rail down the side, and the scenarios by feature — a passed one folded to a
 * line, a failed or running one open on its specification.
 */
const props = defineProps<{ runId: string }>()

const runs = useRunsStore()
const model = useEventModelStore()
const route = useRoute()
const run = computed(() => runs.runById(props.runId))
const { runners } = useRunners()
const now = useNow(1000)

// bobcat#784: the board hydrates from a summary per run; this page is where a run's events are
// wanted, so it asks for the archive — once, and not at all for a run first seen live.
watch(() => props.runId, (runId) => void ensureRunEvents(runId), { immediate: true })

/** bobcat#652: this run is of the code before the runner's last rebuild. */
const stale = computed(() => (run.value ? rebuiltSince(run.value, runners.value) : undefined))
const scenarios = computed(() => (run.value ? visibleScenarios(run.value) : []))

onMounted(() => {
  // Specification → slice (stoat#61) needs the model; nothing else on this page does.
  if (model.status === 'idle') void model.load()
})

/**
 * bobcat#645: this run's selection, to run again — every specification it ran, whatever happened
 * to them. A supervised foreign test has no feature, so no identity a runner knows.
 */
const selection = computed(() => scenarios.value.filter((s) => s.feature).map((s) => s.uid))

/** stoat#78: what "re-run failed" runs. A supervised foreign test has no feature, so no identity a runner knows. */
const failed = computed(() =>
  scenarios.value.filter((s) => (s.status === 'failed' || s.status === 'aborted') && s.feature).map((s) => s.uid),
)

function isFailed(status: string): boolean {
  return status === 'failed' || status === 'aborted'
}

function sliceOf(uid: string) {
  return sliceFor(model.descriptor, uid)
}

// --- Identity (bobcat#787): what the header says about the run beyond its suite.

/** The command that caused this run (bobcat#646), from the pushed runner list; undefined for a run nobody commanded. */
const commanded = computed(() => commandForRun(runners.value, props.runId))

/** The specifications a commanded run was asked for: on the rail before they are reached. */
const expected = computed(() => commanded.value?.command.specs ?? [])

/** `supervised · Run 3 in feature Bank · warm · from bobcat-ui`: the run's mode, and the command's when there is one. */
const modeLine = computed(() => {
  if (!run.value) return ''
  const parts = [run.value.mode]
  if (commanded.value) parts.push(describeCommand(commanded.value.command, commanded.value.runner))
  return parts.join(' · ')
})

/** One labelled clock: how long a finished run took, how long a live one has been going, or when an orphan was last heard. */
const clock = computed(() => {
  const r = run.value
  if (!r) return null
  if (r.finished) {
    const took = formatDuration(r.startedAt, r.finishedAt)
    return took ? `took ${took}` : null
  }
  if (r.orphaned) {
    const heard = formatRelative(r.lastEventAt, now.value)
    return heard ? `orphaned — last heard ${heard}` : 'orphaned'
  }
  const elapsed = formatDuration(r.startedAt, new Date(now.value).toISOString())
  return elapsed ? `running for ${elapsed}` : null
})

const startedAt = computed(() => (run.value ? formatAbsolute(run.value.startedAt) : null))
const startedAgo = computed(() => (run.value ? formatRelative(run.value.startedAt, now.value) : null))

/** The repository's name when the server resolved one; the checkout path is its tooltip. */
const repositoryName = computed(() => {
  if (!run.value) return null
  const slug = runs.repositoryOf(run.value)
  return slug ? (slug.split('/').pop() ?? slug) : null
})

const crumbs = computed(() => (run.value ? [runsCrumb(), suiteCrumb(run.value.suite), runCrumb(run.value, true, now.value)] : []))

/** A live run's bar is the board's (bobcat#786): the same computation, so the two never disagree. */
const live = computed(() => !!run.value && !run.value.finished && !run.value.orphaned)
const plannedHint = computed(() => (run.value ? plannedHintOf(run.value, runners.value) : null))
const commandLine = computed(() => (run.value ? commandLineOf(run.value, runners.value) : null))
/** The suite's earlier runs, for the bar's planned total and its estimate. */
const previousRuns = computed(() => {
  const r = run.value
  if (!r) return []
  return runs.runsNewestFirst.filter((other) => other.runId !== r.runId && other.suite === r.suite && other.repository === r.repository)
})

// --- The tree, the tiles and the rail.

const tree = computed(() => buildRunTree(scenarios.value, expected.value))
const summary = computed(() => (run.value ? summarize(run.value, scenarios.value, expected.value) : null))
const running = computed(() => runningScenario(scenarios.value))
const byUid = computed(() => new Map(scenarios.value.map((s) => [s.uid, s])))

const filter = ref<TocStatus | null>(null)
const follow = ref(false)

/**
 * The features as the page lists them, in the rail's order and narrowed by the rail's filter, each
 * node joined to its scenario — null for a specification the command named that has not reported.
 */
const sections = computed(() =>
  tree.value.features
    .map((feature) => ({
      ...feature,
      items: feature.scenarios
        .filter((node) => filter.value === null || node.status === filter.value)
        .map((node) => ({ node, scenario: byUid.value.get(node.uid) ?? null })),
    }))
    .filter((feature) => feature.items.length > 0),
)

function toggleFilter(status: TocStatus): void {
  filter.value = filter.value === status ? null : status
}

// The DOM, typed by shape: the vitest tsconfig has no DOM lib, and this component is checked under it.
type Scrollable = { scrollIntoView?: (options: { block: 'start' | 'nearest'; behavior: 'smooth' }) => void }
type Doc = { getElementById(id: string): Scrollable | null }

/** Scroll the page to an anchor — a feature's section or a scenario's block — once it is rendered. */
async function jump(anchor: string, block: 'start' | 'nearest' = 'start'): Promise<void> {
  await nextTick()
  const doc = (globalThis as { document?: Doc }).document
  const target = doc?.getElementById(anchor)
  if (target && typeof target.scrollIntoView === 'function') target.scrollIntoView({ block, behavior: 'smooth' })
}

/** "Go to running": the scenario in flight, shown whatever the filter hides. */
function goToRunning(): void {
  const current = running.value
  if (!current) return
  if (filter.value !== null && filter.value !== 'running') filter.value = null
  void jump(scenarioAnchor(current.uid))
}

// Follow: whenever the scenario in flight changes, or grows a step, keep it in view. Turning follow
// on goes there at once.
watch(
  [() => follow.value, () => running.value?.uid ?? null, () => running.value?.steps.length ?? 0],
  ([following, uid]) => {
    if (following && uid) void jump(scenarioAnchor(uid), 'start')
  },
)

// A link into a feature (`#feature:Wallet`, the spec page's crumb) lands on its section once the
// run's events are on the page, and `#running` (the board's and the strip's "Go to running",
// bobcat#786) is the rail's own button pressed on arrival.
watch(
  () => [run.value?.eventsLoaded ?? false, route.hash] as const,
  ([loaded, hash]) => {
    if (!loaded || !hash) return
    if (hash === '#running') goToRunning()
    else void jump(decodeURIComponent(hash.slice(1)))
  },
  { immediate: true },
)

// --- Expanded or folded, per scenario: a passed one is a line, a failed or running one is open.

/** The reader's own toggles, over the default; forgotten when the scenario's status changes. */
const opened = reactive(new Map<string, boolean>())
const lastStatus = new Map<string, string>()

watch(
  scenarios,
  (list) => {
    for (const s of list) {
      const previous = lastStatus.get(s.uid)
      if (previous !== undefined && previous !== s.status) opened.delete(s.uid)
      lastStatus.set(s.uid, s.status)
    }
  },
  { immediate: true },
)

function expanded(scenario: ScenarioState): boolean {
  return opened.get(scenario.uid) ?? startsExpanded(scenario.status)
}

function onToggle(uid: string, event: Event): void {
  const target = event.target as { open?: boolean } | null
  if (typeof target?.open === 'boolean') opened.set(uid, target.open)
}

const GLYPHS: Record<string, string> = {
  running: '▶',
  'retry-scheduled': '↻',
  passed: '✓',
  'passed-on-retry': '✓',
  failed: '✗',
  aborted: '✗',
  'acceptance-failing': '◌',
}

function glyphOf(status: string): string {
  return GLYPHS[status] ?? '•'
}

function durationOf(scenario: ScenarioState): string | null {
  const ms = scenario.durationMs
  if (ms === null) return null
  return ms < 1000 ? `${ms}ms` : `${(ms / 1000).toFixed(1)}s`
}
</script>

<template>
  <div v-if="!run">
    <MissingRun :run-id="runId" />
  </div>
  <div v-else>
    <Breadcrumbs :crumbs="crumbs" />

    <h2 class="bm-run-heading">
      {{ run.suite }}
      <RunButton v-if="failed.length > 0" :target="{ specs: failed }" :label="`Re-run failed (${failed.length})`" :suite="run.suite" />
      <RunButton
        v-if="selection.length > 0"
        :target="{ specs: selection }"
        :label="`Run again (${selection.length})`"
        :suite="run.suite"
      />
    </h2>

    <!-- The run's identity (bobcat#787): one line a reader can tell this run from the last by. -->
    <dl class="bm-run-identity" data-testid="run-identity">
      <div v-if="startedAt" class="bm-run-fact">
        <dt>started</dt>
        <dd :title="run.startedAt" data-testid="run-started">{{ startedAt }}<span v-if="startedAgo" class="bm-run-ago"> · {{ startedAgo }}</span></dd>
      </div>
      <div v-if="clock" class="bm-run-fact">
        <dt>clock</dt>
        <dd data-testid="run-clock">{{ clock }}</dd>
      </div>
      <div v-if="run.tag" class="bm-run-fact">
        <dt>tag</dt>
        <dd data-testid="run-tag">{{ run.tag }}</dd>
      </div>
      <div class="bm-run-fact">
        <dt>checkout</dt>
        <dd :title="run.repository" data-testid="run-checkout">
          <span v-if="repositoryName">{{ repositoryName }}</span><span v-else class="bm-run-path">{{ run.repository }}</span>
          <span v-if="run.branch"> @ {{ run.branch }}</span>
        </dd>
      </div>
      <div class="bm-run-fact">
        <dt>mode</dt>
        <dd data-testid="run-mode">{{ modeLine }}</dd>
      </div>
      <div v-if="run.session" class="bm-run-fact">
        <dt>session</dt>
        <dd data-testid="run-session" :title="run.session">{{ run.session }}</dd>
      </div>
      <el-tag v-if="stale" size="small" type="warning" data-testid="before-rebuild" :title="`Rebuilt ${stale.builtAt}`">
        from before the last rebuild
      </el-tag>
      <el-tag v-if="run.orphaned" size="small" type="warning" title="The publisher went away and the run never finished.">orphaned</el-tag>
    </dl>

    <!-- Summary tiles: the run in five numbers. A tile is also the rail's filter. -->
    <div v-if="summary" class="bm-run-tiles" data-testid="run-tiles">
      <button type="button" class="bm-tile" data-status="passed" data-testid="tile-passed" :data-active="filter === 'passed' || undefined" @click="toggleFilter('passed')">
        <span class="bm-tile-count">{{ summary.passed }}</span><span class="bm-tile-label">passed</span>
      </button>
      <button type="button" class="bm-tile" data-status="failed" data-testid="tile-failed" :data-active="filter === 'failed' || undefined" @click="toggleFilter('failed')">
        <span class="bm-tile-count">{{ summary.failed }}</span><span class="bm-tile-label">failed</span>
      </button>
      <button type="button" class="bm-tile" data-status="running" data-testid="tile-running" :data-active="filter === 'running' || undefined" @click="toggleFilter('running')">
        <span class="bm-tile-count">{{ summary.running }}</span><span class="bm-tile-label">running</span>
      </button>
      <button type="button" class="bm-tile" data-status="pending" data-testid="tile-pending" :data-active="filter === 'pending' || undefined" @click="toggleFilter('pending')">
        <span class="bm-tile-count">{{ summary.pending }}</span><span class="bm-tile-label">no result yet</span>
      </button>
      <div v-if="summary.passedOnRetry > 0" class="bm-tile" data-status="retried" data-testid="tile-retried">
        <span class="bm-tile-count">{{ summary.passedOnRetry }}</span><span class="bm-tile-label">passed on retry</span>
      </div>
      <div v-if="summary.acceptanceFailing > 0" class="bm-tile" data-status="retried" data-testid="tile-acceptance">
        <span class="bm-tile-count">{{ summary.acceptanceFailing }}</span><span class="bm-tile-label">acceptance not yet passing</span>
      </div>
    </div>
    <LiveRunProgress
      v-if="live"
      class="bm-run-live"
      :run="run"
      :planned-hint="plannedHint"
      :command="commandLine"
      :previous="previousRuns"
    />

    <SupervisorTopology :run="run" />

    <div class="bm-run-layout">
      <aside class="bm-run-rail">
        <RunToc
          v-model:filter="filter"
          v-model:follow="follow"
          :suite="run.suite"
          :tree="tree"
          :running="running?.uid ?? null"
          @jump="(anchor) => jump(anchor)"
          @go-to-running="goToRunning"
        />
      </aside>

      <div class="bm-run-body">
        <p v-if="scenarios.length === 0 && expected.length === 0" class="bm-run-empty" data-testid="no-scenarios">
          {{ run.eventsLoaded ? 'No scenario has reported yet.' : 'Loading the run…' }}
        </p>

        <section v-for="feature in sections" :id="feature.anchor" :key="feature.anchor" class="bm-feature" :data-status="feature.status" data-testid="feature">
          <h3 class="bm-feature-heading">
            {{ feature.name || '(no feature)' }}
            <span class="bm-feature-counts">
              <span v-if="feature.counts.failed" class="bm-count-failed">{{ feature.counts.failed }} failed</span>
              <span v-if="feature.counts.running" class="bm-count-running">{{ feature.counts.running }} running</span>
              <span v-if="feature.counts.pending" class="bm-count-pending">{{ feature.counts.pending }} to go</span>
              <span v-if="feature.counts.passed" class="bm-count-passed">{{ feature.counts.passed }} passed</span>
            </span>
          </h3>

          <template v-for="{ node, scenario } in feature.items" :key="node.uid">
            <!-- A specification the command named that the run has not reached (bobcat#787). -->
            <div v-if="!scenario" :id="scenarioAnchor(node.uid)" class="bm-scenario bm-scenario-pending" data-status="pending" data-testid="pending-scenario">
              <span class="bm-scenario-glyph">○</span> {{ node.title }} <span class="bm-scenario-muted">no result yet</span>
            </div>
            <details
              v-else
              :id="scenarioAnchor(node.uid)"
              class="bm-scenario"
              :data-status="scenario.status"
              :data-uid="node.uid"
              :open="expanded(scenario)"
              data-testid="scenario"
              @toggle="onToggle(node.uid, $event)"
            >
              <summary class="bm-scenario-title">
                <span class="bm-scenario-glyph" aria-hidden="true">{{ glyphOf(scenario.status) }}</span>
                <!-- A supervised foreign test (issue #195) has no feature — it is a test name, not a
                     spec — so nothing invents one for it. -->
                <router-link :to="specHref(run.runId, node.uid)" class="bm-spec-link" data-testid="spec-link" @click.stop>
                  {{ scenario.scenario }}
                </router-link>
                <el-tag v-if="scenario.state" size="small">{{ scenario.state }}</el-tag>
                <!-- A press on the button must not fold the scenario: the summary's own toggle is its
                     default action, cancelled here; the button's handler has already run. -->
                <span v-if="isFailed(scenario.status) && scenario.feature" @click.prevent>
                  <RunButton :target="{ specs: [node.uid] }" label="Re-run" text :suite="run.suite" />
                </span>
                <el-tag v-if="scenario.attempt > 1" size="small" type="warning">attempt {{ scenario.attempt }}</el-tag>
                <el-tag v-if="scenario.status === 'retry-scheduled'" size="small" type="warning">
                  retrying — {{ scenario.retryReason }}
                </el-tag>
                <router-link
                  v-if="sliceOf(node.uid)"
                  :to="sliceHref(sliceOf(node.uid)!.name, node.uid, run.runId)"
                  class="bm-slice-link"
                  data-testid="slice-link"
                  @click.stop
                >
                  slice {{ sliceOf(node.uid)!.name }}
                </router-link>
                <span v-if="durationOf(scenario)" class="bm-scenario-duration">{{ durationOf(scenario) }}</span>
              </summary>
              <template v-if="expanded(scenario)">
                <ScenarioProgress :scenario="scenario" />
                <!-- stoat#61 — the one specification renderer, the same one the spec page and the Event
                     Model drill-down use. -->
                <SpecificationView :scenario="scenario" />
              </template>
            </details>
          </template>
        </section>
      </div>
    </div>
  </div>
</template>

<style scoped>
.bm-run-heading {
  display: flex;
  align-items: center;
  gap: 12px;
  margin: 0 0 8px;
}

.bm-run-identity {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px 18px;
  margin: 0 0 12px;
  font-size: 12px;
  color: var(--bm-menu-text);
}

.bm-run-fact {
  display: flex;
  gap: 6px;
  min-width: 0;
}

.bm-run-fact dt {
  color: var(--bc-neutral-500);
}

.bm-run-fact dd {
  margin: 0;
  color: var(--bc-neutral-300);
  max-width: 48ch;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.bm-run-ago {
  color: var(--bc-neutral-500);
}

.bm-run-tiles {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.bm-tile {
  display: flex;
  flex-direction: column;
  min-width: 96px;
  padding: 6px 12px;
  border: 1px solid var(--bc-neutral-800);
  border-radius: 6px;
  background: transparent;
  color: inherit;
  font: inherit;
  text-align: left;
}

button.bm-tile {
  cursor: pointer;
}

button.bm-tile:hover,
.bm-tile[data-active='true'] {
  border-color: var(--bc-neutral-600);
}

.bm-tile-count {
  font-size: 20px;
  line-height: 1.1;
}

.bm-tile-label {
  font-size: 11px;
  color: var(--bm-menu-text);
}

.bm-tile[data-status='passed'] .bm-tile-count {
  color: var(--bm-state-passed);
}

.bm-tile[data-status='failed'] .bm-tile-count {
  color: var(--bm-state-failed);
}

.bm-tile[data-status='running'] .bm-tile-count {
  color: var(--bm-state-running);
}

.bm-tile[data-status='pending'] .bm-tile-count {
  color: var(--bm-state-unreached);
}

.bm-tile[data-status='retried'] .bm-tile-count {
  color: var(--bm-state-retrying);
}

.bm-run-live {
  margin-top: 10px;
}

.bm-run-layout {
  display: grid;
  grid-template-columns: 260px minmax(0, 1fr);
  gap: 20px;
  align-items: start;
  margin-top: 16px;
}

.bm-run-rail {
  position: sticky;
  top: 12px;
  max-height: calc(100vh - 24px);
  overflow: auto;
  padding-right: 6px;
}

.bm-run-empty {
  font-size: 12px;
  color: var(--bm-menu-text);
}

.bm-feature {
  margin-bottom: 18px;
  scroll-margin-top: 12px;
}

.bm-feature-heading {
  display: flex;
  align-items: baseline;
  gap: 12px;
  margin: 0 0 6px;
  font-size: 15px;
}

.bm-feature-counts {
  display: inline-flex;
  gap: 10px;
  font-size: 12px;
}

.bm-count-failed {
  color: var(--bm-state-failed);
}

.bm-count-running {
  color: var(--bm-state-running);
}

.bm-count-pending {
  color: var(--bm-state-unreached);
}

.bm-count-passed {
  color: var(--bm-state-passed);
}

.bm-scenario {
  border-left: 3px solid var(--bm-state-running);
  padding: 6px 12px;
  margin-bottom: 8px;
  scroll-margin-top: 12px;
}

.bm-scenario[data-status='passed'] {
  border-color: var(--bm-state-passed);
}

.bm-scenario[data-status='passed-on-retry'],
.bm-scenario[data-status='retry-scheduled'],
.bm-scenario[data-status='acceptance-failing'] {
  border-color: var(--bm-state-retrying);
}

.bm-scenario[data-status='failed'],
.bm-scenario[data-status='aborted'] {
  border-color: var(--bm-state-failed);
}

.bm-scenario[data-status='pending'] {
  border-color: var(--bm-state-unreached);
  font-size: 13px;
}

.bm-scenario-title {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  font-weight: 600;
  cursor: pointer;
  list-style: none;
}

.bm-scenario-title::-webkit-details-marker {
  display: none;
}

.bm-scenario-glyph {
  width: 1em;
  text-align: center;
}

.bm-scenario[data-status='passed'] .bm-scenario-glyph,
.bm-scenario[data-status='passed-on-retry'] .bm-scenario-glyph {
  color: var(--bm-state-passed);
}

.bm-scenario[data-status='failed'] .bm-scenario-glyph,
.bm-scenario[data-status='aborted'] .bm-scenario-glyph {
  color: var(--bm-state-failed);
}

.bm-scenario[data-status='running'] .bm-scenario-glyph {
  color: var(--bm-state-running);
}

.bm-scenario-duration,
.bm-scenario-muted {
  font-size: 12px;
  font-weight: 400;
  color: var(--bm-menu-text);
}

.bm-scenario-duration {
  margin-left: auto;
}

.bm-spec-link {
  color: inherit;
  text-decoration: none;
}

.bm-spec-link:hover {
  text-decoration: underline;
}

.bm-slice-link {
  font-size: 12px;
  font-weight: 400;
  color: var(--bm-link);
}
</style>
