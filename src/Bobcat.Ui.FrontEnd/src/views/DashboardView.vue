<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { storeToRefs } from 'pinia'
import { useRunsStore, type RunState } from '@/stores/runs-store'
import { useBoardStore, UNATTRIBUTED } from '@/stores/board-store'
import { useRunners } from '@/composables/useRunners'
import SuiteRow from '@/components/SuiteRow.vue'
import { describeRetention, groupBySuite, loadExpandedSuites, saveExpandedSuites } from '@/lib/runBoard'

/**
 * The run board (bobcat#786): one row per suite — repository plus suite name — expanding to the
 * suite's retained runs, live suites first and then by their most recent run. It replaced a flat
 * list of cards in which one busy suite buried every other, and the global "Show N" with it: the
 * number of rows is the number of suites on the box, and the server's retention bounds each one.
 */
const runs = useRunsStore()
const board = useBoardStore()
const { repositoryFilter } = storeToRefs(board)
const { runners } = useRunners()

onMounted(() => void board.loadRetentionOnce())

/** A run still being published to: shows its bar, is never ejectable, sorts its suite first. */
function isLive(run: RunState): boolean {
  return !run.finished && !run.orphaned
}

/**
 * The repository filter (stoat#76): every repository on the board, by its canonical slug, plus
 * "unattributed" for runs from a path the server could not resolve. Empty is every repository.
 */
const repositoryChoices = computed(() => {
  const slugs = new Set<string>()
  let unattributed = false
  for (const run of runs.allRuns) {
    const slug = runs.repositoryOf(run)
    if (slug) slugs.add(slug)
    else if (slug === null) unattributed = true
  }
  for (const runner of runners.value ?? []) {
    if (runner.repositorySlug) slugs.add(runner.repositorySlug)
  }
  return [...[...slugs].sort((a, b) => a.localeCompare(b)), ...(unattributed ? [UNATTRIBUTED] : [])]
})

function matchesRepository(run: RunState): boolean {
  if (!repositoryFilter.value) return true
  const slug = runs.repositoryOf(run)
  return repositoryFilter.value === UNATTRIBUTED ? slug === null : slug === repositoryFilter.value
}

/** Every suite, grouped; then the filter, which keeps a runner-only suite when its slug matches. */
const suites = computed(() => {
  const groups = groupBySuite(runs.runsNewestFirst, runners.value, (run) => runs.repositoryOf(run), isLive)
  const filter = repositoryFilter.value
  if (!filter) return groups
  return groups.filter((group) =>
    group.runs.length > 0
      ? group.runs.some(matchesRepository)
      : filter !== UNATTRIBUTED && group.runner?.repositorySlug === filter,
  )
})

/** The repository's short name for a row's pill; null when the path is all the board knows. */
function repositoryNameOf(repository: string): string | null {
  const known = Object.values(runs.slugByPath).includes(repository) || (runners.value ?? []).some((r) => r.repositorySlug === repository)
  return known ? repository.split('/').pop() ?? repository : null
}

const retention = computed(() => describeRetention(board.retention))

// Which suites this viewer has open, remembered in their browser.
const expanded = ref(loadExpandedSuites())
function toggle(key: string) {
  const next = new Set(expanded.value)
  if (next.has(key)) next.delete(key)
  else next.add(key)
  expanded.value = next
  saveExpandedSuites(next)
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
        // The row shows the path until the next try.
      }
    }, 1000)
  },
  { immediate: true },
)
onUnmounted(() => {
  if (learning !== null) clearTimeout(learning)
})

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
        <span v-if="retention" class="bm-retention" data-testid="retention">{{ retention }}</span>
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
        <el-button v-if="ejectableCount > 1" size="small" class="bm-eject-all" @click="ejectMany(allScope())">
          Eject all ({{ ejectableCount }})
        </el-button>
      </div>
    </div>
    <el-empty v-if="suites.length === 0" description="No runs yet — waiting for a publisher" />
    <SuiteRow
      v-for="group in suites"
      :key="group.key"
      :group="group"
      :expanded="expanded.has(group.key)"
      :repository-name="repositoryNameOf(group.repository)"
      @toggle="toggle(group.key)"
      @filter-repository="repositoryFilter = group.repository"
      @eject="eject"
      @eject-older="ejectMany(olderThanScope($event))"
      @eject-others="ejectMany(allButScope($event))"
    />
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

.bm-retention {
  font-size: 12px;
  color: var(--bm-menu-text);
}

.bm-repository-filter {
  width: 200px;
}
</style>
