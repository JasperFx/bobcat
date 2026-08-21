<script setup lang="ts">
import { computed } from 'vue'
import type { ScenarioState, StepState } from '@/stores/runs-store'

/**
 * Where a scenario in flight is, from the step-level progress events (issue #99): which
 * step of how many, and — when that step is a [TableGrammar] or a [WaitFor] — which row of
 * how many, or what the poll loop last saw and how long it has been waiting. Renders nothing
 * once the scenario has a verdict; the step list below it already tells the finished story.
 */
const props = defineProps<{ scenario: ScenarioState }>()

const inFlight = computed(() => props.scenario.status === 'running')

/** The step currently running — the last one without a verdict. */
const current = computed<StepState | null>(() => {
  const steps = props.scenario.steps
  for (let i = steps.length - 1; i >= 0; i--) {
    const step = steps[i]!
    if (step.status === 'running') return step
  }
  return null
})

/** 1-based position of the current step: what the publisher said, else its index. */
const stepNumber = computed<number | null>(() => {
  const step = current.value
  if (!step) return null
  if (step.stepNumber != null) return step.stepNumber
  return props.scenario.steps.indexOf(step) + 1
})

const totalSteps = computed<number | null>(() => props.scenario.totalSteps)

/** Completed steps over the announced total, in [0, 1]; null when the total is unknown. */
const fraction = computed<number | null>(() => {
  const total = totalSteps.value
  if (!total || total <= 0) return null
  const done = props.scenario.steps.filter((s) => s.status !== 'running').length
  return Math.min(1, done / total)
})

const percent = computed(() => (fraction.value === null ? 0 : Math.round(fraction.value * 100)))

const stepLabel = computed(() => {
  const n = stepNumber.value
  const total = totalSteps.value
  if (n === null) return null
  return total ? `step ${n} of ${total}` : `step ${n}`
})

const progress = computed(() => current.value?.progress ?? null)

const rowLabel = computed(() => {
  const p = progress.value
  if (!p || p.row == null) return null
  return p.totalRows ? `row ${p.row} of ${p.totalRows}` : `row ${p.row}`
})

const rowPercent = computed(() => {
  const p = progress.value
  if (!p || p.row == null || !p.totalRows) return null
  return Math.round((100 * p.row) / p.totalRows)
})

const waitingLabel = computed(() => {
  const p = progress.value
  if (!p || p.message === null) return null
  return p.message
})

function formatElapsed(ms: number): string {
  if (ms < 1000) return `${ms}ms`
  const seconds = ms / 1000
  if (seconds < 60) return `${seconds.toFixed(seconds < 10 ? 1 : 0)}s`
  const minutes = Math.floor(seconds / 60)
  return `${minutes}m ${Math.round(seconds - minutes * 60)}s`
}

const elapsedLabel = computed(() => {
  const p = progress.value
  if (!p) return null
  return formatElapsed(p.elapsedMs)
})
</script>

<template>
  <div v-if="inFlight && current" class="bm-progress" data-testid="scenario-progress">
    <div class="bm-progress-line">
      <span v-if="stepLabel" class="bm-progress-step" data-testid="step-label">{{ stepLabel }}</span>
      <span class="bm-progress-text">
        <strong>{{ current.kind }}</strong> {{ current.text }}
      </span>
    </div>
    <el-progress
      v-if="fraction !== null"
      :percentage="percent"
      :stroke-width="6"
      :show-text="false"
      class="bm-progress-bar"
      data-testid="step-bar"
    />
    <div v-if="rowLabel" class="bm-progress-detail" data-testid="row-label">
      {{ rowLabel }}
      <el-progress
        v-if="rowPercent !== null"
        :percentage="rowPercent"
        :stroke-width="4"
        :show-text="false"
        class="bm-progress-bar bm-progress-bar-rows"
        data-testid="row-bar"
      />
    </div>
    <div v-if="waitingLabel" class="bm-progress-detail bm-progress-waiting" data-testid="waiting-label">
      {{ waitingLabel }}
      <span v-if="elapsedLabel" class="bm-progress-elapsed" data-testid="elapsed-label">
        {{ elapsedLabel }}
      </span>
    </div>
  </div>
</template>

<style scoped>
.bm-progress {
  margin: 4px 0 8px;
  padding: 6px 10px;
  border-radius: 4px;
  background: var(--bm-state-running-bg);
  font-size: 12px;
}

.bm-progress-line {
  display: flex;
  gap: 10px;
  align-items: baseline;
}

.bm-progress-step {
  color: var(--bm-state-running);
  font-weight: 600;
  white-space: nowrap;
}

.bm-progress-text {
  color: var(--bm-menu-text);
}

.bm-progress-bar {
  margin-top: 4px;
}

.bm-progress-bar-rows {
  max-width: 240px;
}

.bm-progress-detail {
  margin-top: 4px;
  color: var(--bm-menu-text);
}

.bm-progress-waiting {
  font-style: italic;
}

.bm-progress-elapsed {
  margin-left: 8px;
  font-style: normal;
  color: var(--bm-state-running);
}
</style>
