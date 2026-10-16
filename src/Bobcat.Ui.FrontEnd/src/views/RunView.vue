<script setup lang="ts">
import { computed, onMounted } from 'vue'
import ScenarioProgress from '@/components/ScenarioProgress.vue'
import SpecificationView from '@/components/SpecificationView.vue'
import { useRunsStore, visibleScenarios } from '@/stores/runs-store'
import SupervisorTopology from '@/components/SupervisorTopology.vue'
import RunButton from '@/components/RunButton.vue'
import { sliceFor, useEventModelStore } from '@/stores/event-model-store'
import { sliceHref, specHref } from '@/lib/specLinks'

const props = defineProps<{ runId: string }>()

const runs = useRunsStore()
const model = useEventModelStore()
const run = computed(() => runs.runById(props.runId))
const scenarios = computed(() => (run.value ? visibleScenarios(run.value) : []))

onMounted(() => {
  // Specification → slice (stoat#61) needs the model; nothing else on this page does.
  if (model.status === 'idle') void model.load()
})

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
</script>

<template>
  <div v-if="!run">
    <el-empty description="Unknown run" />
  </div>
  <div v-else>
    <h2 class="bm-run-heading">
      {{ run.suite }}
      <RunButton v-if="failed.length > 0" :target="{ specs: failed }" :label="`Re-run failed (${failed.length})`" />
    </h2>
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
        <router-link :to="specHref(run.runId, scenario.uid)" class="bm-spec-link" data-testid="spec-link">
          <span v-if="scenario.feature">{{ scenario.feature }}: </span>{{ scenario.scenario }}
        </router-link>
        <el-tag v-if="scenario.state" size="small">{{ scenario.state }}</el-tag>
        <RunButton v-if="isFailed(scenario.status) && scenario.feature" :target="{ specs: [scenario.uid] }" label="Re-run" text />
        <el-tag v-if="scenario.attempt > 1" size="small" type="warning">
          attempt {{ scenario.attempt }}
        </el-tag>
        <el-tag v-if="scenario.status === 'retry-scheduled'" size="small" type="warning">
          retrying — {{ scenario.retryReason }}
        </el-tag>
        <router-link
          v-if="sliceOf(scenario.uid)"
          :to="sliceHref(sliceOf(scenario.uid)!.name, scenario.uid, run.runId)"
          class="bm-slice-link"
          data-testid="slice-link"
        >
          slice {{ sliceOf(scenario.uid)!.name }}
        </router-link>
      </div>
      <ScenarioProgress :scenario="scenario" />
      <!-- stoat#61 — the one specification renderer, the same one the spec page and the Event
           Model drill-down use. -->
      <SpecificationView :scenario="scenario" />
    </div>
  </div>
</template>

<style scoped>
.bm-run-heading {
  display: flex;
  align-items: center;
  gap: 12px;
}

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
.bm-scenario[data-status='retry-scheduled'],
.bm-scenario[data-status='acceptance-failing'] {
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

.bm-spec-link {
  color: inherit;
  text-decoration: none;
}

.bm-spec-link:hover {
  text-decoration: underline;
}

.bm-slice-link {
  margin-left: 8px;
  font-size: 12px;
  font-weight: 400;
  color: var(--bm-link);
}
</style>
