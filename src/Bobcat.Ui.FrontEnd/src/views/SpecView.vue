<script setup lang="ts">
import { computed, onMounted } from 'vue'
import ScenarioProgress from '@/components/ScenarioProgress.vue'
import SpecificationView from '@/components/SpecificationView.vue'
import RunButton from '@/components/RunButton.vue'
import { useRunsStore } from '@/stores/runs-store'
import { sliceFor, useEventModelStore } from '@/stores/event-model-store'
import { sliceHref } from '@/lib/specLinks'

/**
 * One specification from one run, on a page of its own (stoat#61) — the target a slice's
 * drill-down links to, and a URL that can be pasted into a PR. Renders through the same
 * SpecificationView the run page uses, so the two can never show the same spec differently.
 */
const props = defineProps<{ runId: string; uid: string }>()

const runs = useRunsStore()
const model = useEventModelStore()

onMounted(() => {
  // The slice link needs the model; a page reached by a hard navigation has not loaded it yet.
  if (model.status === 'idle') void model.load()
})

const run = computed(() => runs.runById(props.runId))
const scenario = computed(() => run.value?.scenarios[props.uid])
const slice = computed(() => sliceFor(model.descriptor, props.uid))

const verdictType = computed<'success' | 'danger' | 'warning' | 'info'>(() => {
  const status = scenario.value?.status
  if (status === 'passed') return 'success'
  if (status === 'failed' || status === 'aborted') return 'danger'
  if (status === 'passed-on-retry' || status === 'retry-scheduled') return 'warning'
  return 'info'
})
</script>

<template>
  <div>
    <div class="bm-spec-crumbs">
      <router-link :to="`/runs/${runId}`" data-testid="run-link">{{ run?.suite ?? 'run' }}</router-link>
      <template v-if="slice">
        ·
        <router-link :to="sliceHref(slice.name, uid, runId)" data-testid="slice-link">
          slice {{ slice.name }}
        </router-link>
      </template>
    </div>

    <el-empty v-if="!scenario" description="This run has no such specification" data-testid="unknown-spec" />
    <template v-else>
      <h2 class="bm-spec-title">
        <span v-if="scenario.feature" class="bm-spec-feature">{{ scenario.feature }}: </span>{{ scenario.scenario }}
        <el-tag size="small" :type="verdictType" data-testid="spec-verdict">
          {{ scenario.outcome ?? scenario.status }}
        </el-tag>
        <el-tag v-if="scenario.attempt > 1" size="small" type="warning">attempt {{ scenario.attempt }}</el-tag>
      </h2>
      <!-- stoat#78: run it again, or its whole feature, on a resident runner. -->
      <div class="bm-spec-actions">
        <RunButton :target="{ specs: [uid] }" label="Run again" />
        <RunButton v-if="scenario.feature" :target="{ feature: scenario.feature }" :label="`Run ${scenario.feature}`" />
      </div>
      <ScenarioProgress :scenario="scenario" />
      <SpecificationView :scenario="scenario" />
    </template>
  </div>
</template>

<style scoped>
.bm-spec-crumbs {
  font-size: 12px;
  color: var(--bm-menu-text);
  margin-bottom: 8px;
}

.bm-spec-crumbs a {
  color: var(--bm-link);
}

.bm-spec-actions {
  display: flex;
  gap: 8px;
  margin-bottom: 12px;
}

.bm-spec-feature {
  color: var(--bm-menu-text);
  font-weight: 400;
}

.bm-spec-title {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
</style>
