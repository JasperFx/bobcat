<script setup lang="ts">
/**
 * The Event Model page (issue #108): renders the pushed descriptor through the shared
 * @jasperfx/event-model-vue renderer — the same component CritterWatch consumes, which is what
 * makes "the same descriptor renders identically in both viewers" true by construction —
 * colours slices from a run's evidence (issue #107), and drills down from a clicked slice to
 * its bound scenarios with their step results.
 */
import { computed, onMounted, ref } from 'vue'
import {
  EventModelView,
  type EventModelElement,
  type EventModelSliceDescriptor
} from '@jasperfx/event-model-vue'
import '@jasperfx/event-model-vue/style.css'
import { outcomesFor, undeclaredTouches, useEventModelStore } from '@/stores/event-model-store'
import { useRunsStore, type RunState, type ScenarioState } from '@/stores/runs-store'

const model = useEventModelStore()
const runs = useRunsStore()

onMounted(() => {
  if (model.status === 'idle') void model.load()
})

// Evidence source: newest run first, user-switchable. The join is by spec identity, so any
// run of the bound suite colours the canvas; an empty box means design-time only.
const selectedRunId = ref<string | null>(null)
const runChoices = computed<RunState[]>(() =>
  [...runs.allRuns].sort((a, b) => (b.startedAt ?? '').localeCompare(a.startedAt ?? ''))
)
const evidenceRun = computed<RunState | undefined>(() =>
  selectedRunId.value ? runs.runById(selectedRunId.value) : runChoices.value[0]
)

const sliceOutcomes = computed(() =>
  evidenceRun.value ? outcomesFor(model.descriptor, evidenceRun.value.scenarios) : undefined
)

// ---------------------------------------------------------------- drill-down

const drilled = ref<EventModelSliceDescriptor | null>(null)

function openSlice(slice: EventModelSliceDescriptor) {
  drilled.value = slice
}

// A click on any card drills into the slice that owns it — the element ids on the wire are
// unique per slice, so ownership is a lookup, not a parse.
function openElement(element: EventModelElement) {
  const owner = model.slices.find((s) => (s.elements ?? []).some((e) => e.id === element.id))
  if (owner) drilled.value = owner
}

interface BoundSpec {
  identity: string
  scenario: ScenarioState | undefined
  undeclared: string[]
}

const boundSpecs = computed<BoundSpec[]>(() =>
  (drilled.value?.specifications ?? []).map((spec) => {
    const scenario = evidenceRun.value?.scenarios[spec.identity]
    return {
      identity: spec.identity,
      scenario,
      undeclared: drilled.value ? undeclaredTouches(drilled.value, scenario) : []
    }
  })
)

function verdictType(scenario: ScenarioState | undefined): 'success' | 'danger' | 'warning' | 'info' {
  if (!scenario || !scenario.outcome) return 'warning' // not run (or still running) — the drift colour
  if (scenario.outcome === 'CleanPass') return 'success'
  if (scenario.outcome === 'PassOnRetry') return 'warning'
  return 'danger'
}

function verdictLabel(scenario: ScenarioState | undefined): string {
  return scenario?.outcome ?? 'not run'
}
</script>

<template>
  <div>
    <div class="bm-em-header">
      <h2>Event Model<span v-if="model.descriptor"> — {{ model.descriptor.name }}</span></h2>
      <el-select
        v-if="runChoices.length > 0"
        :model-value="evidenceRun?.runId ?? null"
        placeholder="run evidence"
        size="small"
        class="bm-em-run-select"
        data-testid="evidence-run"
        @update:model-value="(v: string) => (selectedRunId = v)"
      >
        <el-option
          v-for="run in runChoices"
          :key="run.runId"
          :value="run.runId"
          :label="`${run.suite} — ${new Date(run.startedAt).toLocaleString()}`"
        />
      </el-select>
    </div>

    <el-empty
      v-if="model.status === 'absent'"
      data-testid="event-model-absent"
      description="No Event Model has been published yet."
    >
      <p class="bm-em-hint">
        Push one with
        <code>curl -X PUT --data @event-model.json http://localhost:5525/api/event-model</code>
        — the file Wolverine's <code>event-model</code> export writes, or a descriptor a Bobcat
        spec assembly reported.
      </p>
    </el-empty>

    <el-alert
      v-else-if="model.status === 'error'"
      type="error"
      title="Could not load the Event Model."
      :closable="false"
    />

    <EventModelView
      v-else-if="model.descriptor"
      :descriptor="model.descriptor"
      :slice-outcomes="sliceOutcomes"
      @slice-click="openSlice"
      @element-click="openElement"
    />

    <el-drawer
      :model-value="drilled !== null"
      :title="drilled?.name"
      size="40%"
      @close="drilled = null"
    >
      <div v-if="drilled" data-testid="slice-drilldown">
        <p v-if="boundSpecs.length === 0" class="bm-em-nospec" data-testid="no-specs">
          No specification is bound to this slice — the orange of drift colouring.
        </p>

        <div
          v-for="bound in boundSpecs"
          :key="bound.identity"
          class="bm-em-spec"
          :data-spec="bound.identity"
        >
          <div class="bm-em-spec-title">
            {{ bound.identity }}
            <el-tag size="small" :type="verdictType(bound.scenario)" data-testid="spec-verdict">
              {{ verdictLabel(bound.scenario) }}
            </el-tag>
          </div>

          <ul v-if="bound.scenario" class="bm-em-steps">
            <li
              v-for="step in bound.scenario.steps"
              :key="step.stepId"
              :data-status="step.status"
              class="bm-em-step"
            >
              <span class="bm-em-step-kind">{{ step.kind }}</span>
              {{ step.text }}
              <span v-if="step.durationMs != null" class="bm-em-step-ms">{{ step.durationMs }}ms</span>
              <div v-if="step.errorMessage" class="bm-em-step-error">{{ step.errorMessage }}</div>
            </li>
          </ul>

          <div v-if="bound.scenario && bound.scenario.touchedTypes.length > 0" class="bm-em-touched">
            <span class="bm-em-touched-label">touched:</span>
            <el-tag
              v-for="touched in bound.scenario.touchedTypes"
              :key="touched.fullName"
              size="small"
              :type="bound.undeclared.includes(touched.fullName) ? 'warning' : 'info'"
              :title="touched.fullName"
              class="bm-em-touched-tag"
            >
              {{ touched.name }}
            </el-tag>
            <span
              v-if="bound.undeclared.length > 0"
              class="bm-em-undeclared"
              data-testid="undeclared-touches"
            >
              {{ bound.undeclared.length }} touched type(s) the model does not declare
            </span>
          </div>
        </div>
      </div>
    </el-drawer>
  </div>
</template>

<style scoped>
.bm-em-header {
  display: flex;
  align-items: center;
  gap: 16px;
}

.bm-em-run-select {
  width: 320px;
}

.bm-em-hint {
  max-width: 560px;
  font-size: 13px;
  opacity: 0.8;
}

.bm-em-spec {
  margin-bottom: 20px;
}

.bm-em-spec-title {
  font-weight: 600;
  margin-bottom: 6px;
}

.bm-em-steps {
  margin: 0 0 8px;
  padding-left: 18px;
  font-size: 13px;
}

.bm-em-step[data-status='failed'],
.bm-em-step[data-status='error'] {
  color: var(--el-color-danger);
}

.bm-em-step-kind {
  font-weight: 600;
  margin-right: 4px;
}

.bm-em-step-ms {
  opacity: 0.6;
  margin-left: 6px;
  font-size: 12px;
}

.bm-em-step-error {
  font-size: 12px;
  opacity: 0.85;
}

.bm-em-touched {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  font-size: 12px;
}

.bm-em-touched-label {
  opacity: 0.7;
}

.bm-em-undeclared {
  color: var(--el-color-warning);
}

.bm-em-nospec {
  opacity: 0.75;
}
</style>
