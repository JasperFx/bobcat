<script setup lang="ts">
import { computed } from 'vue'
import type { StepCell } from '@/messages/monitor-events'
import { cellsOf, verdictOf } from '@/composables/specification'

/**
 * A scenario report (bobcat#408): a titled grid of what the system did. Its rows are unjudged
 * values, so it states nothing passed; a row a report did judge is coloured like any other cell.
 */
const props = defineProps<{ title: string; columns: string[]; cells: StepCell[]; suppressedRows: number }>()

const table = computed(() => cellsOf(props.cells, props.columns).table)

function text(cell: StepCell | null): string {
  if (!cell) return ''
  if (cell.value != null) return cell.value
  if (verdictOf(cell) === 'passed') return cell.actual ?? cell.expected ?? ''
  if (cell.expected != null || cell.actual != null) return `expected '${cell.expected ?? ''}', got '${cell.actual ?? ''}'`
  return cell.note ?? cell.status
}

function verdict(cell: StepCell | null): string {
  if (!cell || (cell.status === 'ok' && cell.expected == null && cell.actual == null)) return 'none'
  return verdictOf(cell)
}
</script>

<template>
  <section class="bm-report" data-testid="report">
    <h4>{{ title }}</h4>
    <table v-if="table" class="bm-report-grid">
      <thead>
        <tr>
          <th v-for="column in table.columns" :key="column">{{ column }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in table.rows" :key="row.index">
          <td v-for="(cell, i) in row.cells" :key="i" :data-verdict="verdict(cell)">{{ text(cell) }}</td>
        </tr>
      </tbody>
    </table>
    <p v-if="suppressedRows > 0" class="bm-report-more">…and {{ suppressedRows }} more</p>
  </section>
</template>

<style scoped>
.bm-report h4 {
  margin: 8px 0 4px;
  font-size: 12px;
  color: var(--bm-menu-text);
}

.bm-report-grid {
  border-collapse: collapse;
  font-size: 12px;
}

.bm-report-grid th,
.bm-report-grid td {
  border: 1px solid var(--bm-divider);
  padding: 2px 8px;
  text-align: left;
}

.bm-report-grid td[data-verdict='passed'] {
  color: var(--bm-state-passed);
}

.bm-report-grid td[data-verdict='failed'],
.bm-report-grid td[data-verdict='errored'] {
  background: var(--bm-state-failed-bg);
  color: var(--bm-state-failed);
}

.bm-report-more {
  font-size: 11px;
  opacity: 0.7;
  margin: 2px 0;
}
</style>
