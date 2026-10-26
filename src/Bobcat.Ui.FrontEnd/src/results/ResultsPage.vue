<script setup lang="ts">
import { computed, ref } from 'vue'
import AttachmentList from '@/components/AttachmentList.vue'
import SpecificationView from '@/components/SpecificationView.vue'
import {
  failsTheRun,
  featuresOf,
  summaryOf,
  type ResultsPageData,
  type ResultsScenarioView,
} from './fromResults'
import type { ResultsScenarioTiming } from '@/messages/results-file'

/**
 * The HTML results page (bobcat#541): one run, archived. Storyteller's `stresults.htm` for Bobcat —
 * a file CI uploads and someone opens later, with no server behind it. Live is the board's job.
 *
 * Every scenario renders through the board's own `SpecificationView`, so what a reader sees here is
 * what `bobcat ui` showed while the run was live. A failing scenario starts open; the rest start
 * closed, because a page of a thousand passing specifications is read for its failures first.
 */
const props = defineProps<{ data: ResultsPageData; error: string | null }>()

const features = computed(() => featuresOf(props.data))
const summary = computed(() => summaryOf(props.data))
const runs = computed(() => props.data.files.map((f) => f.results.run).filter((r) => r !== undefined))
const suite = computed(() => [...new Set(runs.value.map((r) => r.suite).filter(Boolean))].join(', ') || 'Bobcat')
const schemaVersion = computed(() => props.data.files[0]?.results.schemaVersion ?? null)

const failingOnly = ref(false)
const search = ref('')

function matches(scenario: ResultsScenarioView): boolean {
  if (failingOnly.value && !failsTheRun(scenario) && scenario.status !== 'acceptance-failing') return false
  const term = search.value.trim().toLowerCase()
  return term.length === 0 || scenario.id.toLowerCase().includes(term)
}

const shown = computed(() =>
  features.value
    .map((group) => ({ ...group, scenarios: group.scenarios.filter(matches) }))
    .filter((group) => group.scenarios.length > 0),
)

const startedAt = computed(() => {
  const times = runs.value.map((r) => r.startedAt).filter((t): t is string => !!t).sort()
  return times[0] ? new Date(times[0]).toLocaleString() : null
})

const durationMs = computed(() => {
  const starts = runs.value.map((r) => Date.parse(r.startedAt ?? '')).filter((t) => !Number.isNaN(t))
  const ends = runs.value.map((r) => Date.parse(r.finishedAt ?? '')).filter((t) => !Number.isNaN(t))
  return starts.length && ends.length ? Math.max(...ends) - Math.min(...starts) : null
})

/** The slowest scenarios, across every file, by bracket wall clock. */
const slowest = computed<ResultsScenarioTiming[]>(() =>
  props.data.files
    .flatMap((f) => f.results.timing?.scenarios ?? [])
    .sort((a, b) => (b.wallClockMs ?? 0) - (a.wallClockMs ?? 0))
    .slice(0, 10),
)

const GLYPHS: Record<string, string> = {
  passed: '✓',
  failed: '✗',
  'acceptance-failing': '◌',
}

function glyphOf(scenario: ResultsScenarioView): string {
  return GLYPHS[scenario.status] ?? '•'
}

function seconds(ms: number | null): string {
  if (ms === null) return ''
  return ms < 1000 ? `${ms}ms` : `${(ms / 1000).toFixed(1)}s`
}

function describe(cell: { expected: string | null; actual: string | null; value: string | null; note: string | null }): string {
  if (cell.expected != null || cell.actual != null) return `expected '${cell.expected ?? ''}', got '${cell.actual ?? ''}'`
  return cell.value ?? cell.note ?? ''
}
</script>

<template>
  <div class="bm-results">
    <header class="bm-results-header">
      <h1>{{ suite }}</h1>
      <div
        class="bm-results-verdict"
        :data-succeeded="summary.succeeded"
        data-testid="run-verdict"
      >
        {{ summary.succeeded ? 'Succeeded' : 'Failed' }}
      </div>
      <dl class="bm-results-facts">
        <template v-if="startedAt">
          <dt>Started</dt>
          <dd>{{ startedAt }}</dd>
        </template>
        <template v-if="durationMs !== null">
          <dt>Took</dt>
          <dd>{{ seconds(durationMs) }}</dd>
        </template>
        <template v-for="run in runs.slice(0, 1)" :key="run.runId">
          <template v-if="run.branch">
            <dt>Branch</dt>
            <dd>{{ run.branch }}</dd>
          </template>
          <template v-if="run.repository">
            <dt>Repository</dt>
            <dd>{{ run.repository }}</dd>
          </template>
          <template v-if="run.tag">
            <dt>Tag</dt>
            <dd>{{ run.tag }}</dd>
          </template>
          <template v-if="run.mode">
            <dt>Runner</dt>
            <dd>{{ run.mode }}</dd>
          </template>
        </template>
        <template v-if="data.files.length > 1">
          <dt>Results files</dt>
          <dd>{{ data.files.length }}</dd>
        </template>
      </dl>
      <p class="bm-results-counts" data-testid="run-counts">
        {{ summary.scenarios }} specifications: {{ summary.passed }} passed, {{ summary.failed }} failed<template
          v-if="summary.acceptanceFailing"
          >, {{ summary.acceptanceFailing }} acceptance not yet passing</template
        >
        · {{ summary.rights }} right, {{ summary.wrongs }} wrong, {{ summary.errors }} error{{ summary.errors === 1 ? '' : 's' }}
      </p>
    </header>

    <p v-if="error" class="bm-results-error" data-testid="page-error">{{ error }}</p>
    <p v-else-if="data.files.length === 0" class="bm-results-empty" data-testid="no-results">
      This page holds no results.
    </p>

    <nav v-if="features.length" class="bm-results-filter">
      <label><input v-model="failingOnly" type="checkbox" data-testid="failing-only" /> failing only</label>
      <input v-model="search" type="search" placeholder="filter by feature or scenario" data-testid="search" />
    </nav>

    <section v-for="group in shown" :key="group.feature" class="bm-feature" data-testid="feature">
      <h2>
        {{ group.feature || 'Specifications' }}
        <span class="bm-feature-counts">{{ group.scenarios.length }}<template v-if="group.failing">, {{ group.failing }} failing</template></span>
      </h2>

      <details
        v-for="scenario in group.scenarios"
        :id="scenario.id"
        :key="scenario.id"
        class="bm-scenario"
        :data-status="scenario.status"
        :open="failsTheRun(scenario)"
        data-testid="scenario"
      >
        <summary>
          <span class="bm-scenario-glyph" aria-hidden="true">{{ glyphOf(scenario) }}</span>
          {{ scenario.title }}
          <span class="bm-scenario-verdict">{{ scenario.verdict }}</span>
          <span class="bm-scenario-duration">{{ seconds(scenario.durationMs) }}</span>
        </summary>

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
      </details>
    </section>

    <details v-if="slowest.length" class="bm-timing" data-testid="timing">
      <summary>Slowest specifications</summary>
      <table>
        <thead>
          <tr>
            <th>Specification</th>
            <th>Wall clock</th>
            <th>Steps</th>
            <th>Outside any step</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(t, i) in slowest" :key="i">
            <td>{{ t.feature }} / {{ t.title }}</td>
            <td>{{ seconds(t.wallClockMs ?? null) }}</td>
            <td>{{ seconds(t.stepsMs ?? null) }}</td>
            <td>{{ seconds(t.unownedMs ?? null) }}</td>
          </tr>
        </tbody>
      </table>
    </details>

    <footer class="bm-results-footer">
      Written by Bobcat<template v-if="schemaVersion"> from results file version {{ schemaVersion }}</template>.
    </footer>
  </div>
</template>

<style>
html,
body {
  margin: 0;
  background: var(--bc-ground);
  color: var(--bc-neutral-200);
  font-family: system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif;
  font-size: 14px;
}
</style>

<style scoped>
.bm-results {
  max-width: 1200px;
  margin: 0 auto;
  padding: 16px 24px 48px;
}

.bm-results-header h1 {
  margin: 0 0 4px;
  font-size: 22px;
  color: var(--bc-coat);
}

.bm-results-verdict {
  display: inline-block;
  padding: 2px 10px;
  border-radius: 4px;
  font-weight: 600;
  background: var(--bm-state-failed-bg);
  color: var(--bm-state-failed);
}

.bm-results-verdict[data-succeeded='true'] {
  background: var(--bm-state-passed-bg);
  color: var(--bm-state-passed);
}

.bm-results-facts {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 2px 12px;
  margin: 10px 0;
  font-size: 12px;
}

.bm-results-facts dt {
  color: var(--bm-menu-text);
}

.bm-results-facts dd {
  margin: 0;
}

.bm-results-counts {
  margin: 4px 0 12px;
}

.bm-results-error,
.bm-results-empty {
  color: var(--bm-state-failed);
}

.bm-results-filter {
  display: flex;
  gap: 16px;
  align-items: center;
  margin: 8px 0 16px;
  font-size: 13px;
}

.bm-results-filter input[type='search'] {
  flex: 1;
  max-width: 420px;
  background: var(--bc-surface);
  color: inherit;
  border: 1px solid var(--bm-divider);
  border-radius: 4px;
  padding: 4px 8px;
}

.bm-feature h2 {
  font-size: 16px;
  margin: 20px 0 6px;
  border-bottom: 1px solid var(--bc-accent-800);
  padding-bottom: 4px;
  color: var(--bc-coat);
}

.bm-feature-counts {
  font-size: 12px;
  font-weight: normal;
  color: var(--bm-menu-text);
  margin-left: 8px;
}

.bm-scenario {
  background: var(--bc-surface);
  border-left: 3px solid var(--bm-state-passed);
  border-radius: 0 4px 4px 0;
  margin: 4px 0;
  padding: 4px 10px;
}

.bm-scenario[data-status='failed'] {
  border-left-color: var(--bm-state-failed);
}

.bm-scenario[data-status='acceptance-failing'] {
  border-left-color: var(--bm-state-retrying);
}

.bm-scenario summary {
  cursor: pointer;
}

.bm-scenario-glyph {
  display: inline-block;
  width: 1em;
}

.bm-scenario[data-status='passed'] .bm-scenario-glyph {
  color: var(--bm-state-passed);
}

.bm-scenario[data-status='failed'] .bm-scenario-glyph {
  color: var(--bm-state-failed);
}

.bm-scenario[data-status='acceptance-failing'] .bm-scenario-glyph {
  color: var(--bm-state-retrying);
}

.bm-scenario-verdict,
.bm-scenario-duration {
  margin-left: 8px;
  font-size: 12px;
  color: var(--bm-menu-text);
}

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

.bm-timing {
  margin-top: 24px;
  font-size: 12px;
}

.bm-timing summary {
  cursor: pointer;
  font-weight: 600;
}

.bm-timing table {
  border-collapse: collapse;
  margin-top: 6px;
}

.bm-timing th,
.bm-timing td {
  border: 1px solid var(--bm-divider);
  padding: 2px 8px;
  text-align: left;
}

.bm-results-footer {
  margin-top: 32px;
  font-size: 11px;
  color: var(--bm-menu-text);
}
</style>
