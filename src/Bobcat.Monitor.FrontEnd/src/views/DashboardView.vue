<script setup lang="ts">
import { useRunsStore, type RunState } from '@/stores/runs-store'

const runs = useRunsStore()

function progressPercent(run: RunState): number {
  const p = runs.progressOf(run)
  return p === null ? 0 : Math.round(p * 100)
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
      <el-progress
        v-if="!run.finished"
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
      <el-button
        v-if="run.finished"
        size="small"
        class="bm-eject"
        @click="runs.removeRun(run.runId)"
      >
        Eject
      </el-button>
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

.bm-eject {
  margin-top: 8px;
}
</style>
