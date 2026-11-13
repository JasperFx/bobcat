<script setup lang="ts">
import AttachmentList from '@/components/AttachmentList.vue'
import SpecificationView from '@/components/SpecificationView.vue'
import type { ResultsScenarioView } from './fromResults'

/**
 * One scenario from a results file, below its title: the specification through the board's own
 * `SpecificationView`, then what the file carries at the scenario level — the failure cells
 * recovered from an assertion library's message, the lines logged between steps, the attachments
 * on the scenario rather than a step. The HTML results page (bobcat#541) renders every scenario
 * through this, and so does the `<bobcat-spec>` element (bobcat#553), so the two cannot drift.
 */
defineProps<{ scenario: ResultsScenarioView }>()

function describe(cell: { expected: string | null; actual: string | null; value: string | null; note: string | null }): string {
  if (cell.expected != null || cell.actual != null) return `expected '${cell.expected ?? ''}', got '${cell.actual ?? ''}'`
  return cell.value ?? cell.note ?? ''
}
</script>

<template>
  <div class="bm-scenario-body">
    <SpecificationView :scenario="scenario" />

    <ul v-if="scenario.scenarioFailureCells.length" class="bm-failure-cells" data-testid="scenario-failure-cells">
      <li v-for="(cell, i) in scenario.scenarioFailureCells" :key="i">
        <strong>{{ cell.name }}:</strong> {{ describe(cell) }}
      </li>
    </ul>

    <ul v-if="scenario.logs.length" class="bm-scenario-logs" data-testid="scenario-logs">
      <li v-for="(line, i) in scenario.logs" :key="i">{{ line }}</li>
      <li v-if="scenario.suppressedLogs">…and {{ scenario.suppressedLogs }} more</li>
    </ul>

    <AttachmentList
      v-if="scenario.attachments.length || scenario.suppressedAttachments"
      :attachments="scenario.attachments"
      :suppressed="scenario.suppressedAttachments"
    />
  </div>
</template>

<style scoped>
.bm-scenario-body {
  padding: 6px 0 4px;
}

.bm-failure-cells,
.bm-scenario-logs {
  list-style: none;
  padding-left: 8px;
  font-size: 12px;
}

.bm-failure-cells {
  color: var(--bm-state-failed);
}

.bm-scenario-logs {
  font-family: ui-monospace, monospace;
  font-size: 11px;
  color: var(--bm-menu-text);
}
</style>
