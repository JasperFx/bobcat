<script setup lang="ts">
import { computed } from 'vue'
import type { StepCell } from '@/messages/monitor-events'
import {
  cellsOf,
  segmentsOf,
  shortTypeName,
  verdictOf,
  type RowMarker,
  type SpecLine,
  type TextSegment,
} from '@/composables/specification'

/**
 * One line of a rendered specification (stoat#58, #59, #60): a step that ran, with everything it
 * said about itself, or a planned step the run never reached.
 */
const props = defineProps<{ line: SpecLine }>()

const GLYPHS: Record<SpecLine['status'], string> = {
  running: '⋯',
  passed: '✓',
  failed: '✗',
  errored: '!',
  'not-run': '○',
  pending: '○',
}

const glyph = computed(() => GLYPHS[props.line.status])

/**
 * An empty keyword is a step that spells none — a `[Step]` grammar (stoat#59) — and renders as
 * NO label. Falling back to a default here is the bug Bobcat's own console had: `Then` printed
 * over sentences that never claimed to be one.
 */
const keyword = computed(() => (props.line.kind === 'step' ? props.line.step.kind : props.line.planned.keyword))

/**
 * A planned step renders its template UNRESOLVED: its arguments were never evaluated, so there
 * are no values to show, and filling the placeholders in would describe a run that did not happen.
 */
const segments = computed<TextSegment[]>(() =>
  props.line.kind === 'step'
    ? segmentsOf(props.line.step.text, props.line.step.values)
    : [{ text: props.line.planned.template, value: false }],
)

const step = computed(() => (props.line.kind === 'step' ? props.line.step : null))

/**
 * The cells to show: the step's own once it finished, and the ones made SO FAR while it is still
 * running (stoat#62) — a slow step fills in cell by cell instead of showing nothing until it ends.
 */
const cells = computed<StepCell[]>(() => {
  const s = step.value
  if (!s) return []
  if (s.status === 'running') return s.progress?.cells ?? []
  return s.cells
})

const interim = computed(() => step.value?.status === 'running' && cells.value.length > 0)
const split = computed(() => cellsOf(cells.value, step.value?.columns ?? []))

/**
 * The message stays on the line only for a disagreement that has no cells to say it better. An
 * exception's message goes to the panel at the bottom with its stack, and the line gets a marker.
 */
const lineMessage = computed(() => {
  const s = step.value
  if (!s || s.exception || !s.errorMessage) return null
  if (s.cells.some((c) => verdictOf(c) !== 'passed')) return null
  return s.errorMessage
})

const exceptionMarker = computed(() => {
  const exception = step.value?.exception
  if (!exception) return null
  return `${shortTypeName(exception.type) ?? 'Exception'} — see below`
})

const diagnostics = computed(() => Object.entries(step.value?.diagnostics ?? {}))

const hasMarkers = computed(() => split.value.table?.rows.some((r) => r.marker !== null) ?? false)

/**
 * An absent row's value in one column: what was expected for a missing row, what was found for an
 * extra one. A dash where the publisher sent no value — an older producer reported a marker alone,
 * and a blank cell would read as a value that is empty.
 */
function rowValue(marker: RowMarker, cell: StepCell | null): string {
  const value = (marker === 'missing' ? cell?.expected : cell?.actual) ?? cell?.value
  return value == null ? '—' : value
}

function describe(cell: StepCell): string {
  const verdict = verdictOf(cell)
  // `value` is a cell nothing judged — a table literal's input column (bobcat#396, Bobcat 0.30.0).
  if (verdict === 'passed') return cell.actual ?? cell.expected ?? cell.value ?? ''
  if (cell.expected != null || cell.actual != null) {
    return `expected '${cell.expected ?? ''}', got '${cell.actual ?? ''}'`
  }
  return cell.status
}
</script>

<template>
  <li class="bm-line" :data-status="line.status" :data-kind="line.kind" data-testid="spec-line">
    <!-- Real spaces between the parts, each boundary on one template line: Vue drops whitespace
         that spans a newline between elements, and copied text or a screen reader should get
         "✓ Given Start with 3 1ms", not "✓GivenStart with 31ms". -->
    <div class="bm-line-head">
      <span class="bm-glyph" aria-hidden="true">{{ glyph }}</span> <strong v-if="keyword" class="bm-keyword" data-testid="line-keyword">{{ keyword }}</strong> <span class="bm-sentence" data-testid="line-sentence"
        ><template v-for="(segment, i) in segments" :key="i"
          ><em v-if="segment.value" class="bm-value">{{ segment.text }}</em
          ><template v-else>{{ segment.text }}</template></template
        ></span> <span v-if="line.status === 'not-run'" class="bm-unreached" data-testid="not-run">— not run</span><span v-else-if="line.status === 'pending'" class="bm-unreached" data-testid="pending">— pending</span> <span v-if="step && step.durationMs !== null" class="bm-duration">{{ step.durationMs }}ms</span>
    </div>

    <div v-if="exceptionMarker" class="bm-exception-marker" data-testid="exception-marker">
      {{ exceptionMarker }}
    </div>
    <div v-if="lineMessage" class="bm-error" data-testid="line-message">{{ lineMessage }}</div>

    <!-- stoat#58: each cell judged on its own, green beside red. -->
    <ul v-if="split.sentence.length" class="bm-cells" data-testid="cells">
      <li v-if="interim" class="bm-interim" data-testid="interim-cells">so far</li>
      <li
        v-for="(cell, i) in split.sentence"
        :key="`${cell.name}-${i}`"
        class="bm-cell"
        :data-verdict="verdictOf(cell)"
        data-testid="cell"
      >
        <!-- One line on purpose: Vue condenses whitespace across a newline between elements, and
             copied text should read "✓ Sum: …", not "✓Sum: …". -->
        <span class="bm-glyph" aria-hidden="true">{{ verdictOf(cell) === 'passed' ? '✓' : '✗' }}</span> <span class="bm-cell-name">{{ cell.name }}:</span> {{ describe(cell) }}
        <span v-if="cell.note" class="bm-cell-note">{{ cell.note }}</span>
      </li>
    </ul>

    <table v-if="split.table" class="bm-cell-grid" data-testid="cell-grid">
      <caption v-if="interim" class="bm-interim">so far</caption>
      <thead>
        <tr>
          <th v-if="hasMarkers" aria-label="row"></th>
          <th v-for="column in split.table.columns" :key="column">{{ column }}</th>
        </tr>
      </thead>
      <tbody>
        <!-- stoat#63: a row absent from one side is ONE failure, marked on the row. Its cells are
             `ok` on the wire only so the row counts once — they are the row's values, never passes. -->
        <tr
          v-for="row in split.table.rows"
          :key="row.index"
          :data-row="row.index"
          :data-marker="row.marker ?? undefined"
        >
          <td v-if="hasMarkers" class="bm-row-marker" data-testid="row-marker">
            <template v-if="row.marker">{{ row.marker }}</template>
          </td>
          <td
            v-for="(cell, i) in row.cells"
            :key="i"
            :data-verdict="row.marker ? 'absent' : cell ? verdictOf(cell) : 'none'"
            :title="cell?.note ?? undefined"
          >
            <template v-if="row.marker">{{ rowValue(row.marker, cell) }}</template>
            <template v-else-if="cell">{{ describe(cell) }}</template>
          </td>
        </tr>
      </tbody>
    </table>

    <ul v-if="step && step.logs.length" class="bm-logs" data-testid="logs">
      <li v-for="(log, i) in step.logs" :key="i">{{ log }}</li>
    </ul>

    <dl v-if="diagnostics.length" class="bm-diagnostics" data-testid="diagnostics">
      <template v-for="[name, value] in diagnostics" :key="name">
        <dt>{{ name }}</dt>
        <dd>{{ value }}</dd>
      </template>
    </dl>
  </li>
</template>

<style scoped>
.bm-line {
  margin: 2px 0;
}

/* Inline flow, not flex: a long sentence must wrap beside its keyword the way prose does, rather
   than drop to a line of its own and strand the keyword and duration on theirs. */
.bm-line-head {
  display: block;
}

.bm-glyph {
  display: inline-block;
  width: 1em;
  text-align: center;
}

.bm-line[data-status='passed'] > .bm-line-head > .bm-glyph {
  color: var(--bm-state-passed);
}

.bm-line[data-status='failed'] {
  color: var(--bm-state-failed);
}

.bm-line[data-status='errored'] > .bm-line-head {
  color: var(--bm-state-errored);
}

.bm-line[data-status='running'] > .bm-line-head > .bm-glyph {
  color: var(--bm-state-running);
}

/* stoat#59: a step the run never reached is present — so a reader can tell "asserted four things"
   from "meant to assert six and fell over at the third" — and visibly not evidence. */
.bm-line[data-status='not-run'],
.bm-line[data-status='pending'] {
  color: var(--bm-state-unreached);
}

.bm-unreached {
  font-size: 12px;
  font-style: italic;
}

/* stoat#59: the data in a sentence, set apart from its prose the way Storyteller did. */
.bm-value {
  font-style: italic;
  color: var(--bm-link);
}

.bm-duration {
  color: var(--bm-menu-text);
  font-size: 12px;
}

.bm-error {
  color: var(--bm-state-failed);
  font-size: 12px;
  white-space: pre-wrap;
  padding-left: 1.5em;
}

.bm-exception-marker {
  color: var(--bm-state-errored);
  font-size: 12px;
  padding-left: 1.5em;
}

.bm-cells {
  list-style: none;
  margin: 2px 0;
  padding-left: 1.5em;
  font-size: 12px;
}

.bm-cell[data-verdict='passed'] {
  color: var(--bm-state-passed);
}

.bm-cell[data-verdict='failed'],
.bm-cell[data-verdict='errored'] {
  color: var(--bm-state-failed);
}

.bm-cell-name {
  font-weight: 600;
}

.bm-cell-note {
  margin-left: 6px;
  opacity: 0.75;
}

.bm-interim {
  color: var(--bm-state-running);
  font-style: italic;
  font-size: 11px;
}

.bm-cell-grid {
  margin: 4px 0 4px 1.5em;
  border-collapse: collapse;
  font-size: 12px;
}

.bm-cell-grid th,
.bm-cell-grid td {
  border: 1px solid var(--bm-divider);
  padding: 2px 8px;
  text-align: left;
}

/* A passed cell is green in its own right, as a sentence cell is — not the failing step's red over
   a green tint, which read as neither. */
.bm-cell-grid td[data-verdict='passed'] {
  background: var(--bm-state-passed-bg);
  color: var(--bm-state-passed);
}

.bm-cell-grid td[data-verdict='failed'],
.bm-cell-grid td[data-verdict='errored'] {
  background: var(--bm-state-failed-bg);
  color: var(--bm-state-failed);
}

/* stoat#63: an absent row is a failure of the whole row. A missing row shows what was expected,
   dimmed and italic — it is a description of a row that is not there; an extra row shows what was
   found, at full strength — it is a real row nobody asked for. */
.bm-cell-grid tr[data-marker] td {
  background: var(--bm-state-failed-bg);
}

.bm-cell-grid tr[data-marker='missing'] td[data-verdict='absent'] {
  font-style: italic;
  opacity: 0.75;
}

.bm-cell-grid tr[data-marker='extra'] td[data-verdict='absent'] {
  color: var(--bm-state-failed);
}

.bm-row-marker {
  color: var(--bm-state-failed);
  font-weight: 600;
  white-space: nowrap;
}

.bm-logs {
  margin: 2px 0;
  padding-left: 2.5em;
  font-family: var(--el-font-family-mono, ui-monospace, monospace);
  font-size: 11px;
  color: var(--bm-menu-text);
}

.bm-diagnostics {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 0 8px;
  margin: 2px 0 2px 1.5em;
  font-size: 11px;
  color: var(--bm-menu-text);
}

.bm-diagnostics dt {
  font-weight: 600;
}

.bm-diagnostics dd {
  margin: 0;
}
</style>
