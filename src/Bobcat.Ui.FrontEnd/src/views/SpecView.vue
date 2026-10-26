<script setup lang="ts">
import { computed, onMounted, watch } from 'vue'
import ScenarioProgress from '@/components/ScenarioProgress.vue'
import SpecificationView from '@/components/SpecificationView.vue'
import RunButton from '@/components/RunButton.vue'
import MissingRun from '@/components/MissingRun.vue'
import Breadcrumbs from '@/components/Breadcrumbs.vue'
import { ensureRunEvents } from '@/messages/hydrate'
import { useRunsStore } from '@/stores/runs-store'
import { sliceFor, useEventModelStore } from '@/stores/event-model-store'
import { sliceHref } from '@/lib/specLinks'
import { placeCrumb, runCrumb, runsCrumb, scenarioCrumb, suiteCrumb, type Crumb } from '@/lib/crumbs'
import { parentAnchorOf, parentLabelOf } from '@/lib/runTree'

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

// bobcat#784: the run's events are loaded when a page wants them, and this page does.
watch(() => props.runId, (runId) => void ensureRunEvents(runId), { immediate: true })

const run = computed(() => runs.runById(props.runId))
const scenario = computed(() => run.value?.scenarios[props.uid])
const slice = computed(() => sliceFor(model.descriptor, props.uid))

/**
 * bobcat#787: `Runs › Suite › run (time · tag · branch) › Feature › Scenario`. The run segment names
 * the run, not just its suite, and the feature segment lands on that feature's section of the run
 * page. A scenario the run does not have is still placed under its run.
 */
const crumbs = computed<Crumb[]>(() => {
  const r = run.value
  if (!r) return [runsCrumb()]
  const trail = [runsCrumb(), suiteCrumb(r.suite), runCrumb(r, false)]
  const s = scenario.value
  if (!s) return trail
  return [...trail, placeCrumb(r.runId, parentAnchorOf(s), parentLabelOf(s), false), scenarioCrumb(s.scenario)]
})

const verdictType = computed<'success' | 'danger' | 'warning' | 'info'>(() => {
  const status = scenario.value?.status
  if (status === 'passed') return 'success'
  if (status === 'failed' || status === 'aborted') return 'danger'
  if (status === 'passed-on-retry' || status === 'retry-scheduled' || status === 'acceptance-failing') return 'warning'
  return 'info'
})
</script>

<template>
  <div>
    <Breadcrumbs :crumbs="crumbs" />

    <MissingRun v-if="!run" :run-id="runId" />
    <el-empty v-else-if="!scenario" description="This run has no such specification" data-testid="unknown-spec" />
    <template v-else>
      <h2 class="bm-spec-title">
        <span v-if="scenario.feature" class="bm-spec-feature">{{ scenario.feature }}: </span>{{ scenario.scenario }}
        <el-tag size="small" :type="verdictType" data-testid="spec-verdict">
          {{ scenario.outcome ?? scenario.status }}
        </el-tag>
        <el-tag v-if="scenario.attempt > 1" size="small" type="warning">attempt {{ scenario.attempt }}</el-tag>
        <router-link v-if="slice" :to="sliceHref(slice.name, uid, runId)" class="bm-slice-link" data-testid="slice-link">
          slice {{ slice.name }}
        </router-link>
      </h2>
      <!-- stoat#78: run it again, or its whole feature, on a resident runner. -->
      <div class="bm-spec-actions">
        <RunButton :target="{ specs: [uid] }" label="Run again" :suite="run?.suite" />
        <RunButton v-if="scenario.feature" :target="{ feature: scenario.feature }" :label="`Run ${scenario.feature}`" />
      </div>
      <ScenarioProgress :scenario="scenario" />
      <SpecificationView :scenario="scenario" />
    </template>
  </div>
</template>

<style scoped>
.bm-slice-link {
  font-size: 12px;
  font-weight: 400;
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
