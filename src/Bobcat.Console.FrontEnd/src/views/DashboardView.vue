<script setup lang="ts">
import { useRunsStore, type RunState } from '@/stores/runs-store'
import LaneStrip from '@/components/LaneStrip.vue'

const runs = useRunsStore()

function progressPercent(run: RunState): number {
  const p = runs.progressOf(run)
  return p === null ? 0 : Math.round(p * 100)
}

function exportUrl(run: RunState, format: 'ctrf' | 'junit' | 'ndjson'): string {
  return `/api/runs/${run.runId}/export?format=${format}`
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
</script>

<template>
  <div>
    <h2>Test suites</h2>
    <el-empty v-if="runs.allRuns.length === 0" description="No runs yet — waiting for a publisher" />
    <el-card v-for="run in runs.allRuns" :key="run.runId" class="bm-run-card">
      <router-link :to="{ name: 'run', params: { runId: run.runId } }" class="bm-run-title">
        {{ run.suite }}
      </router-link>
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
        <el-button size="small" class="bm-eject" @click="eject(run)">Eject</el-button>
      </div>
    </el-card>
  </div>
</template>

<style scoped>
.bm-run-card {
  margin-bottom: 12px;
}

.bm-run-title {
  font-weight: 600;
  color: var(--bm-primary);
  text-decoration: none;
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
