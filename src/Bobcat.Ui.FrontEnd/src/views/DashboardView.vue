<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useRunsStore, visibleScenarios, type RunState } from '@/stores/runs-store'
import { useRunners } from '@/composables/useRunners'
import { commandForRun, describeCommand, rebuiltSince } from '@/lib/runners'
import LaneStrip from '@/components/LaneStrip.vue'
import RunnersPanel from '@/components/RunnersPanel.vue'
import BuildStatusBanner from '@/components/BuildStatusBanner.vue'
import { formatAbsolute, formatDuration, formatRelative, useNow } from '@/composables/time'
import { loadRunLimit, RUN_LIMIT_CHOICES, saveRunLimit, visibleRuns } from '@/lib/runBoard'

const runs = useRunsStore()
const now = useNow()
const { runners } = useRunners()

/**
 * What a commanded run was asked to run (bobcat#646), and while it is in flight with no declared
 * total, how far through its selection it is: the runner knows how many specifications it selected,
 * while the run itself publishes no total. A data-driven specification runs a scenario per row, so
 * this counts specifications selected beside scenarios finished rather than claiming a fraction.
 */
/**
 * bobcat#652: the latest finished run of a suite whose runner has been rebuilt since — the board's
 * latest word on that suite is of code that has changed. Older cards are older anyway, so only the
 * latest says so.
 */
function staleLatest(run: RunState): boolean {
  if (!run.finished || !rebuiltSince(run, runners.value)) return false
  return !runs.runsNewestFirst.some(
    (other) => other.runId !== run.runId && other.suite === run.suite && other.repository === run.repository
      && Date.parse(other.startedAt) > Date.parse(run.startedAt),
  )
}

function commandLineOf(run: RunState): string | null {
  const found = commandForRun(runners.value, run.runId)
  if (!found) return null

  const line = describeCommand(found.command, found.runner)
  if (run.finished || run.orphaned || run.totalScenarios) return line

  const done = visibleScenarios(run).filter((s) => s.status !== 'running' && s.status !== 'retry-scheduled').length
  return `${line} · ${done} finished of ${found.command.specs.length} spec${found.command.specs.length === 1 ? '' : 's'} selected`
}

function progressPercent(run: RunState): number {
  const p = runs.progressOf(run)
  return p === null ? 0 : Math.round(p * 100)
}

type ExportFormat = 'ctrf' | 'junit' | 'ndjson'

const EXPORTS: { format: ExportFormat; label: string }[] = [
  { format: 'ctrf', label: 'Export CTRF' },
  { format: 'junit', label: 'Export JUnit' },
  { format: 'ndjson', label: 'Export NDJSON' },
]

function exportUrl(run: RunState, format: ExportFormat): string {
  return `/api/runs/${run.runId}/export?format=${format}`
}


/** A run still being published to — never hidden by the limit, and the only one with a bar. */
function isLive(run: RunState): boolean {
  return !run.finished && !run.orphaned
}

/** How many finished runs this viewer wants on the board; 0 is all. Remembered per browser. */
const limit = ref(loadRunLimit())
watch(limit, saveRunLimit)

/**
 * The repository filter (stoat#76): every repository on the board, by its canonical slug, plus
 * "unattributed" for runs from a path the server could not resolve. Empty is every repository.
 */
const UNATTRIBUTED = '(unattributed)'
const repositoryFilter = ref('')

const repositoryChoices = computed(() => {
  const slugs = new Set<string>()
  let unattributed = false
  for (const run of runs.allRuns) {
    const slug = runs.repositoryOf(run)
    if (slug) slugs.add(slug)
    else if (slug === null) unattributed = true
  }
  return [...[...slugs].sort((a, b) => a.localeCompare(b)), ...(unattributed ? [UNATTRIBUTED] : [])]
})

function matchesRepository(run: RunState): boolean {
  if (!repositoryFilter.value) return true
  const slug = runs.repositoryOf(run)
  return repositoryFilter.value === UNATTRIBUTED ? slug === null : slug === repositoryFilter.value
}

const board = computed(() => visibleRuns(runs.runsNewestFirst.filter(matchesRepository), limit.value, isLive))

/** The repository's name alone on the card; the slug and the local path are the tooltip. */
function repositoryName(run: RunState): string | null {
  const slug = runs.repositoryOf(run)
  return slug ? slug.split('/').pop() ?? slug : null
}

/** Branch and mode — and the local path, when no repository pill stands in for it. */
function metaOf(run: RunState): string {
  const where = [repositoryName(run) ? null : run.repository, run.branch ? `@ ${run.branch}` : null]
    .filter((part) => part !== null)
    .join(' ')
  return [where, run.mode].filter((part) => part).join(' · ')
}

// A run from a path the board has not seen yet: ask the server what repository it resolved to.
// Debounced, because a suite's first events arrive in a burst.
let learning: ReturnType<typeof setTimeout> | null = null
watch(
  () => runs.allRuns.filter((run) => runs.repositoryOf(run) === undefined).length,
  (unknown) => {
    if (unknown === 0 || learning !== null) return
    learning = setTimeout(async () => {
      learning = null
      try {
        const response = await fetch('/api/runs')
        const summaries = response.ok ? await response.json() : null
        if (Array.isArray(summaries)) runs.learnRepositories(summaries)
      } catch {
        // The card shows the path until the next try.
      }
    }, 1000)
  },
  { immediate: true },
)
onUnmounted(() => {
  if (learning !== null) clearTimeout(learning)
})

function limitLabel(choice: number): string {
  return choice === 0 ? 'all' : String(choice)
}

/**
 * The card's age line (issue #196). A finished run is aged from when it finished and a live one
 * from when it started, and the label says which — the same card reading "6m ago" before and
 * after it finishes means two different things, and the reader cannot tell them apart.
 */
function ageOf(run: RunState): string | null {
  const relative = formatRelative(run.finished ? run.finishedAt : run.startedAt, now.value)
  if (relative === null) return null
  return run.finished ? relative : `started ${relative}`
}

function ageTitleOf(run: RunState): string {
  const started = formatAbsolute(run.startedAt)
  const finished = formatAbsolute(run.finishedAt)
  return finished ? `started ${started}\nfinished ${finished}` : `started ${started}`
}

function durationOf(run: RunState): string | null {
  return run.finished ? formatDuration(run.startedAt, run.finishedAt) : null
}

/**
 * A run is ejectable once nothing is publishing to it. A live run's next event recreates the
 * entry server-side, so offering to eject one would promise something the registry undoes.
 */
function ejectable(run: RunState): boolean {
  return run.finished || run.orphaned
}

/**
 * Eject = forget the run server-side (the NDJSON archive stays on disk) and drop the card.
 * The local removal is unconditional — a dead backend must not leave an unremovable card.
 */
async function eject(run: RunState): Promise<void> {
  try {
    await fetch(`/api/runs/${run.runId}`, { method: 'DELETE' })
  } catch {
    // Server-side eject is best-effort from the UI's perspective.
  }
  runs.removeRun(run.runId)
}

/**
 * The three bulk verbs (issue #197), in the browser-tab shape everyone already has a mental
 * model for: close all tabs / close tabs to the right / close other tabs. "Older" rather than
 * "to the right" because this board is time-ordered in a way tab position is not — which is
 * why #196's timestamps had to land first: an "eject all older" whose cards show no age is a
 * button whose effect the user cannot predict.
 */
type BulkScope = { label: string; query: URLSearchParams; matches: (run: RunState) => boolean }

function allScope(): BulkScope {
  return { label: 'Eject all', query: new URLSearchParams(), matches: () => true }
}

function olderThanScope(run: RunState): BulkScope {
  return {
    label: `Eject all older than ${run.suite}`,
    query: new URLSearchParams({ olderThan: run.startedAt }),
    // Strictly older: the run you anchored on survives its own "eject all older".
    matches: (other) => Date.parse(other.startedAt) < Date.parse(run.startedAt),
  }
}

function allButScope(run: RunState): BulkScope {
  return {
    label: `Eject all but ${run.suite}`,
    query: new URLSearchParams({ exceptRunId: run.runId }),
    matches: (other) => other.runId !== run.runId,
  }
}

/**
 * A board filtered by repository ejects what it shows (stoat#83): every bulk verb is narrowed to
 * the filter, on the server by the same rule, and its label says so. The count on the button is
 * the narrowed count, so it never promises more than the board is showing.
 */
function withinFilter(scope: BulkScope): BulkScope {
  const filter = repositoryFilter.value
  if (!filter) return scope
  const query = new URLSearchParams(scope.query)
  if (filter === UNATTRIBUTED) query.set('unattributed', 'true')
  else query.set('repository', filter)
  return {
    label: `${scope.label} in ${filter === UNATTRIBUTED ? 'unattributed runs' : filter}`,
    query,
    matches: (run) => matchesRepository(run) && scope.matches(run),
  }
}

async function ejectMany(unfiltered: BulkScope): Promise<void> {
  const scope = withinFilter(unfiltered)
  const doomed = runs.allRuns.filter((run) => ejectable(run) && scope.matches(run))
  if (doomed.length === 0) {
    ElMessage.info('Nothing to eject — every other run is still live.')
    return
  }

  const live = runs.allRuns.filter((run) => !ejectable(run) && scope.matches(run)).length

  try {
    await ElMessageBox.confirm(
      // Naming what survives is the whole point: a control that reads as "delete 43 test runs"
      // does not get used, and the same control labelled as clearing a board does.
      `Clear ${doomed.length} run${doomed.length === 1 ? '' : 's'} from the board?` +
        ` The NDJSON archives are kept on disk.` +
        (live > 0 ? ` ${live} still running — those stay.` : ''),
      scope.label,
      {
        confirmButtonText: `Eject ${doomed.length}`,
        cancelButtonText: 'Cancel',
        type: 'warning',
        // A bulk action on a shared surface is never the default-focused button.
        autofocus: false,
      },
    )
  } catch {
    return // dismissed
  }

  try {
    const response = await fetch(`/api/runs?${scope.query}`, { method: 'DELETE' })
    if (response.ok) {
      // Drop exactly what the server agreed to take, not what we predicted it would.
      const ejected = (await response.json()) as { runIds: string[] }
      runs.removeRuns(ejected.runIds ?? [])
      return
    }
  } catch {
    // Fall through to the local removal below.
  }

  // Same rule as the single eject: a dead backend must not leave unremovable cards.
  runs.removeRuns(doomed.map((run) => run.runId))
}

const ejectableCount = computed(() => runs.allRuns.filter((run) => ejectable(run) && matchesRepository(run)).length)
</script>

<template>
  <div>
    <div class="bm-board-header">
      <h2>Test suites</h2>
      <div class="bm-board-controls">
        <el-select
          v-if="repositoryChoices.length > 1"
          v-model="repositoryFilter"
          clearable
          size="small"
          placeholder="Every repository"
          class="bm-repository-filter"
          aria-label="Repository"
        >
          <el-option v-for="choice in repositoryChoices" :key="choice" :label="choice" :value="choice" />
        </el-select>
        <label class="bm-limit">
          Show
          <el-select v-model="limit" size="small" class="bm-limit-select" aria-label="Runs to show">
            <el-option v-for="choice in RUN_LIMIT_CHOICES" :key="choice" :label="limitLabel(choice)" :value="choice" />
          </el-select>
        </label>
        <el-button
          v-if="ejectableCount > 1"
          size="small"
          class="bm-eject-all"
          @click="ejectMany(allScope())"
        >
          Eject all ({{ ejectableCount }})
        </el-button>
      </div>
    </div>
    <BuildStatusBanner />
    <RunnersPanel />
    <el-empty v-if="runs.allRuns.length === 0" description="No runs yet — waiting for a publisher" />
    <el-card v-for="run in board.shown" :key="run.runId" class="bm-run-card" :data-live="isLive(run)">
      <div class="bm-run-row">
        <router-link :to="{ name: 'run', params: { runId: run.runId } }" class="bm-run-title">
          {{ run.suite }}
        </router-link>
        <span
          v-if="repositoryName(run)"
          class="bm-run-repo"
          :title="`${runs.repositoryOf(run)} — ${run.repository}`"
          @click="repositoryFilter = runs.repositoryOf(run) ?? ''"
        >{{ repositoryName(run) }}</span>
        <span class="bm-run-meta" :title="run.repository">{{ metaOf(run) }}</span>
        <span v-if="commandLineOf(run)" class="bm-run-command" data-testid="run-command">{{ commandLineOf(run) }}</span>
        <el-tag
          v-if="staleLatest(run)"
          size="small"
          type="warning"
          data-testid="before-rebuild"
          :title="`The runner was rebuilt at ${rebuiltSince(run, runners)!.builtAt} and has not run this since`"
        >
          from before the last rebuild
        </el-tag>
        <span v-if="run.counts" class="bm-run-counts">
          <span class="bm-passed">{{ run.counts.passed }} passed</span>
          <span v-if="run.counts.passedOnRetry > 0" class="bm-retried">
            {{ run.counts.passedOnRetry }} on retry
          </span>
          <span v-if="run.counts.failed > 0" class="bm-failed">{{ run.counts.failed }} failed</span>
          <span v-if="run.counts.indeterminate > 0" class="bm-failed">
            {{ run.counts.indeterminate }} indeterminate
          </span>
          <span v-if="(run.counts.skipped ?? 0) > 0" class="bm-skipped">
            {{ run.counts.skipped }} skipped
          </span>
        </span>
        <el-tag
          v-if="run.orphaned"
          type="warning"
          size="small"
          title="The publisher went away and the run never finished."
        >
          orphaned
        </el-tag>
        <span class="bm-run-age" :title="ageTitleOf(run)">
          {{ ageOf(run) }}
          <span v-if="durationOf(run)" class="bm-run-duration">· took {{ durationOf(run) }}</span>
        </span>
        <!-- Finished or orphaned (bobcat#784): nothing is publishing to either, so both can go. -->
        <template v-if="ejectable(run)">
          <el-button size="small" text class="bm-eject" @click="eject(run)">Eject</el-button>
          <el-dropdown trigger="click" class="bm-run-menu">
            <el-button size="small" text class="bm-run-menu-button" aria-label="More actions for this run">⋯</el-button>
            <template #dropdown>
              <el-dropdown-menu>
                <!-- Exporting is rare (stoat#75), so it sits in this menu beside the bulk ejects
                     rather than as three links on every card — still real links, the same URLs. -->
                <el-dropdown-item v-for="item in EXPORTS" :key="item.format">
                  <a :href="exportUrl(run, item.format)" download class="bm-export">{{ item.label }}</a>
                </el-dropdown-item>
                <el-dropdown-item divided @click="ejectMany(olderThanScope(run))">
                  Eject all older
                </el-dropdown-item>
                <el-dropdown-item @click="ejectMany(allButScope(run))">
                  Eject all but this
                </el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </template>
      </div>
      <LaneStrip :run="run" />
      <el-progress
        v-if="isLive(run)"
        class="bm-run-progress"
        :stroke-width="4"
        :percentage="progressPercent(run)"
        :indeterminate="runs.progressOf(run) === null"
      />
    </el-card>
    <p v-if="board.hidden > 0" class="bm-board-hidden">
      {{ board.hidden }} older run{{ board.hidden === 1 ? '' : 's' }} not shown —
      <el-button size="small" text class="bm-show-all" @click="limit = 0">show all</el-button>
    </p>
  </div>
</template>

<style scoped>
.bm-board-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.bm-board-controls {
  display: flex;
  align-items: center;
  gap: 12px;
}

.bm-limit {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--bm-menu-text);
}

.bm-limit-select {
  width: 72px;
}

/* One line per run (stoat#75): the board is for scanning, so a card is a row, not a panel. */
.bm-run-card {
  margin-bottom: 6px;
}

.bm-run-card :deep(.el-card__body) {
  padding: 8px 14px;
}

.bm-run-row {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
}

.bm-run-title {
  font-weight: 500;
  color: var(--bm-primary);
  text-decoration: none;
  white-space: nowrap;
}

.bm-repository-filter {
  width: 200px;
}

.bm-run-repo {
  flex: none;
  padding: 0 7px;
  border: 1px solid var(--bc-neutral-800);
  border-radius: 999px;
  font-size: 11px;
  line-height: 18px;
  color: var(--bc-neutral-300);
  cursor: pointer;
}

.bm-run-repo:hover {
  border-color: var(--bc-accent-600);
}

.bm-run-meta {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
  color: var(--bm-menu-text);
}

.bm-run-counts {
  display: flex;
  gap: 10px;
  font-size: 13px;
  white-space: nowrap;
}

.bm-run-age {
  font-size: 12px;
  color: var(--bm-menu-text);
  white-space: nowrap;
}

.bm-run-duration {
  margin-left: 4px;
}

.bm-run-progress {
  margin-top: 6px;
}

.bm-passed {
  color: var(--bm-state-passed);
}

.bm-retried {
  color: var(--bm-state-retrying);
}

.bm-failed {
  color: var(--bm-state-failed);
}

.bm-run-command {
  font-size: 12px;
  color: var(--bm-menu-text);
}

.bm-skipped {
  color: var(--bm-state-unreached);
}

.bm-eject,
.bm-run-menu-button {
  margin-left: 0;
}

.bm-run-menu-button {
  font-size: 16px;
  line-height: 1;
}

/* The whole menu row is the link, not just its words. */
.bm-export {
  display: block;
  margin: 0 -16px;
  padding: 0 16px;
  color: inherit;
  text-decoration: none;
}

.bm-board-hidden {
  font-size: 12px;
  color: var(--bm-menu-text);
}
</style>
