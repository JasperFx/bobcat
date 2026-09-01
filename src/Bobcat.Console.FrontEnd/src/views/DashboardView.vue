<script setup lang="ts">
import { computed } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useRunsStore, type RunState } from '@/stores/runs-store'
import LaneStrip from '@/components/LaneStrip.vue'
import { formatAbsolute, formatDuration, formatRelative, useNow } from '@/composables/time'

const runs = useRunsStore()
const now = useNow()

function progressPercent(run: RunState): number {
  const p = runs.progressOf(run)
  return p === null ? 0 : Math.round(p * 100)
}

function exportUrl(run: RunState, format: 'ctrf' | 'junit' | 'ndjson'): string {
  return `/api/runs/${run.runId}/export?format=${format}`
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

async function ejectMany(scope: BulkScope): Promise<void> {
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

const ejectableCount = computed(() => runs.allRuns.filter(ejectable).length)
</script>

<template>
  <div>
    <div class="bm-board-header">
      <h2>Test suites</h2>
      <el-button
        v-if="ejectableCount > 1"
        size="small"
        class="bm-eject-all"
        @click="ejectMany(allScope())"
      >
        Eject all ({{ ejectableCount }})
      </el-button>
    </div>
    <el-empty v-if="runs.allRuns.length === 0" description="No runs yet — waiting for a publisher" />
    <el-card v-for="run in runs.runsNewestFirst" :key="run.runId" class="bm-run-card">
      <div class="bm-run-heading">
        <router-link :to="{ name: 'run', params: { runId: run.runId } }" class="bm-run-title">
          {{ run.suite }}
        </router-link>
        <span class="bm-run-age" :title="ageTitleOf(run)">
          {{ ageOf(run) }}
          <span v-if="durationOf(run)" class="bm-run-duration">· took {{ durationOf(run) }}</span>
        </span>
      </div>
      <div class="bm-run-meta">
        {{ run.repository }}<span v-if="run.branch"> @ {{ run.branch }}</span> · {{ run.mode }}
      </div>
      <LaneStrip :run="run" />
      <el-tag v-if="run.orphaned" type="warning" size="small">
        orphaned — publisher gone, run never finished
      </el-tag>
      <el-progress
        v-else-if="!run.finished"
        :percentage="progressPercent(run)"
        :indeterminate="runs.progressOf(run) === null"
      />
      <div v-if="run.counts" class="bm-run-counts">
        <span class="bm-passed">{{ run.counts.passed }} passed</span>
        <span v-if="run.counts.passedOnRetry > 0" class="bm-retried">
          {{ run.counts.passedOnRetry }} on retry
        </span>
        <span v-if="run.counts.failed > 0" class="bm-failed">{{ run.counts.failed }} failed</span>
        <span v-if="run.counts.indeterminate > 0" class="bm-failed">
          {{ run.counts.indeterminate }} indeterminate
        </span>
      </div>
      <div v-if="run.finished" class="bm-run-actions">
        <a :href="exportUrl(run, 'ctrf')" class="bm-export">CTRF</a>
        <a :href="exportUrl(run, 'junit')" class="bm-export">JUnit</a>
        <a :href="exportUrl(run, 'ndjson')" class="bm-export">NDJSON</a>
        <el-dropdown split-button size="small" class="bm-eject" @click="eject(run)">
          Eject
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item @click="ejectMany(olderThanScope(run))">
                Eject all older
              </el-dropdown-item>
              <el-dropdown-item @click="ejectMany(allButScope(run))">
                Eject all but this
              </el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
      </div>
    </el-card>
  </div>
</template>

<style scoped>
.bm-board-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.bm-run-card {
  margin-bottom: 12px;
}

.bm-run-heading {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 12px;
}

.bm-run-title {
  font-weight: 600;
  color: var(--bm-primary);
  text-decoration: none;
}

.bm-run-age {
  font-size: 12px;
  color: var(--bm-menu-text);
  white-space: nowrap;
}

.bm-run-duration {
  margin-left: 4px;
}

.bm-run-meta {
  font-size: 12px;
  color: var(--bm-menu-text);
  margin: 4px 0 8px;
}

.bm-run-counts {
  display: flex;
  gap: 12px;
  font-size: 13px;
  margin-top: 8px;
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

.bm-run-actions {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-top: 8px;
}

.bm-export {
  font-size: 12px;
  color: var(--bm-primary);
  text-decoration: none;
}

.bm-export:hover {
  text-decoration: underline;
}
</style>
