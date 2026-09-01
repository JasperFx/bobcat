<script setup lang="ts">
import { computed } from 'vue'
import ScenarioProgress from '@/components/ScenarioProgress.vue'
import { useRunsStore } from '@/stores/runs-store'
import SupervisorTopology from '@/components/SupervisorTopology.vue'

const props = defineProps<{ runId: string }>()

const runs = useRunsStore()
const run = computed(() => runs.runById(props.runId))
const scenarios = computed(() => (run.value ? Object.values(run.value.scenarios) : []))
</script>

<template>
  <div v-if="!run">
    <el-empty description="Unknown run" />
  </div>
  <div v-else>
    <h2>{{ run.suite }}</h2>
    <div class="bm-run-meta">
      {{ run.repository }}<span v-if="run.branch"> @ {{ run.branch }}</span>
    </div>

    <SupervisorTopology :run="run" />

    <div
      v-for="scenario in scenarios"
      :key="scenario.uid"
      class="bm-scenario"
      :data-status="scenario.status"
    >
      <div class="bm-scenario-title">
        <!-- A supervised foreign test (issue #195) has no feature — it is a test name, not a
             spec — so nothing invents one for it and the bare colon does not render. -->
        <span v-if="scenario.feature">{{ scenario.feature }}: </span>{{ scenario.scenario }}
        <el-tag v-if="scenario.state" size="small">{{ scenario.state }}</el-tag>
        <el-tag v-if="scenario.attempt > 1" size="small" type="warning">
          attempt {{ scenario.attempt }}
        </el-tag>
        <el-tag v-if="scenario.status === 'retry-scheduled'" size="small" type="warning">
          retrying — {{ scenario.retryReason }}
        </el-tag>
      </div>
      <ScenarioProgress :scenario="scenario" />
      <ul class="bm-steps">
        <li v-for="step in scenario.steps" :key="step.stepId" :data-status="step.status">
          <strong>{{ step.kind }}</strong> {{ step.text }}
          <span v-if="step.durationMs !== null" class="bm-duration">{{ step.durationMs }}ms</span>
          <div v-if="step.errorMessage" class="bm-error">{{ step.errorMessage }}</div>
        </li>
      </ul>
    </div>
  </div>
</template>

<style scoped>
.bm-run-meta {
  font-size: 12px;
  color: var(--bm-menu-text);
  margin-bottom: 16px;
}

.bm-scenario {
  border-left: 3px solid var(--bm-state-running);
  padding: 8px 12px;
  margin-bottom: 12px;
}

.bm-scenario[data-status='passed'] {
  border-color: var(--bm-state-passed);
}

.bm-scenario[data-status='passed-on-retry'],
.bm-scenario[data-status='retry-scheduled'] {
  border-color: var(--bm-state-retrying);
}

.bm-scenario[data-status='failed'],
.bm-scenario[data-status='aborted'] {
  border-color: var(--bm-state-failed);
}

.bm-scenario-title {
  font-weight: 600;
  margin-bottom: 4px;
}

.bm-steps {
  list-style: none;
  padding-left: 8px;
  margin: 0;
}

.bm-steps li[data-status='failed'] {
  color: var(--bm-state-failed);
}

.bm-duration {
  color: var(--bm-menu-text);
  font-size: 12px;
  margin-left: 8px;
}

.bm-error {
  color: var(--bm-state-failed);
  font-size: 12px;
  white-space: pre-wrap;
}
</style>
