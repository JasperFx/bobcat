<script setup lang="ts">
import { computed } from 'vue'
import ReportGrid from '@/components/ReportGrid.vue'
import SpecLine from '@/components/SpecLine.vue'
import { specificationFor, type SpecSource } from '@/composables/specification'

/**
 * One rendered specification (stoat#61), for both authoring lanes. The run view, the standalone
 * spec page and the Event Model's slice drill-down all render through this component, so there is
 * one viewer to improve rather than three drifting apart.
 *
 * Layout, top to bottom: the declared narrative (a marker-comment test's sentences, each with the
 * lines that ran under it), the lines under no sentence — for every other lane, all of them —
 * the scenario's reports (bobcat#408: its own accounts of what the system did, as grids, beneath
 * the steps where the console renderer puts them), and then the exceptions, each once, in a panel
 * of its own (stoat#60). Inline, one exception pushes the rest of the specification off the
 * screen; at the bottom, the reader first sees WHICH step broke, and chooses when to read why.
 *
 * The HTML results page (bobcat#541) renders an archived run through this component too, from a
 * scenario read out of the results file, so an archived page and the live board agree — the
 * reports included, since bobcat#784 folded them into the live store.
 */
const props = defineProps<{ scenario: SpecSource }>()

const spec = computed(() => specificationFor(props.scenario))
const reports = computed(() => props.scenario.reports ?? [])
</script>

<template>
  <div class="bm-spec" data-testid="specification">
    <ul class="bm-lines" data-testid="declared-narrative">
      <!-- #304 — the sentences the test declared, each with the work that ran under it. A row with
           no work reports NOTHING rather than a borrowed number: nothing observed it. -->
      <li
        v-for="row in spec.rows"
        :key="`declared-${row.number}`"
        class="bm-declared"
        :data-status="row.status"
        :data-declared="row.number"
      >
        <strong v-if="row.keyword">{{ row.keyword }}</strong> {{ row.text }}
        <span v-if="row.observedMs !== null" class="bm-duration">{{ row.observedMs }}ms</span>
        <span
          v-else-if="row.status === 'declared' && row.lines.length === 0"
          class="bm-unobserved"
          title="Declared by a marker comment; no [BobcatStep] helper ran inside it, so nothing observed how long it took"
        >
          declared
        </span>
        <ul v-if="row.lines.length" class="bm-lines bm-recorded">
          <SpecLine v-for="line in row.lines" :key="line.key" :line="line" />
        </ul>
      </li>

      <SpecLine v-for="line in spec.outside" :key="line.key" :line="line" />
    </ul>

    <div v-if="spec.scenarioFailure" class="bm-scenario-failure" data-testid="scenario-failure">
      {{ spec.scenarioFailure }}
    </div>

    <!-- bobcat#408 — the scenario's reports, as the results page shows them. -->
    <ReportGrid
      v-for="(report, i) in reports"
      :key="`report-${i}`"
      :title="report.title"
      :columns="report.columns"
      :cells="report.cells"
      :suppressed-rows="report.suppressedRows"
    />

    <!-- stoat#60 — each exception once, at the bottom, titled with the sentence that threw. -->
    <details
      v-for="panel in spec.exceptions"
      :key="panel.key"
      class="bm-exception-panel"
      open
      data-testid="exception-panel"
    >
      <summary class="bm-exception-title">{{ panel.title }}</summary>
      <div class="bm-exception-body">
        <div class="bm-exception-headline" :title="panel.type ?? undefined">
          <strong>{{ panel.shortType ?? 'Exception' }}</strong
          ><template v-if="panel.message">: {{ panel.message }}</template>
        </div>
        <pre v-if="panel.frames.length" class="bm-frames" data-testid="exception-frames">{{
          panel.frames.join('\n')
        }}</pre>
        <div v-if="panel.hiddenFrames > 0" class="bm-hidden-frames" data-testid="hidden-frames">
          ({{ panel.hiddenFrames }} framework frame{{ panel.hiddenFrames === 1 ? '' : 's' }} hidden)
        </div>
        <!-- The raw stack, for "show every frame" — and the only stack there is when the publisher
             sent no filtered one. -->
        <details v-if="panel.stackTrace && panel.frames.length" class="bm-raw-stack">
          <summary>show every frame</summary>
          <pre class="bm-frames">{{ panel.stackTrace }}</pre>
        </details>
        <pre v-else-if="panel.stackTrace" class="bm-frames">{{ panel.stackTrace }}</pre>
      </div>
    </details>
  </div>
</template>

<style scoped>
.bm-lines {
  list-style: none;
  padding-left: 8px;
  margin: 0;
}

.bm-recorded {
  padding-left: 16px;
  font-size: 13px;
}

/* A declared row nothing ran under is neither passing nor failing — it is a sentence with no
   evidence, and it has to look like one rather than like a step that quietly passed. */
.bm-declared[data-status='declared'] {
  color: var(--bm-menu-text);
}

.bm-declared[data-status='failed'] > strong {
  color: var(--bm-state-failed);
}

.bm-unobserved {
  font-size: 12px;
  margin-left: 8px;
  opacity: 0.6;
}

.bm-duration {
  color: var(--bm-menu-text);
  font-size: 12px;
  margin-left: 8px;
}

.bm-scenario-failure {
  margin-top: 6px;
  color: var(--bm-state-failed);
  font-size: 12px;
  white-space: pre-wrap;
}

.bm-exception-panel {
  margin-top: 10px;
  border-left: 3px solid var(--bm-state-errored);
  background: var(--bm-state-errored-bg);
  border-radius: 0 4px 4px 0;
  padding: 6px 10px;
}

.bm-exception-title {
  cursor: pointer;
  font-weight: 600;
  color: var(--bm-state-errored);
}

.bm-exception-body {
  margin-top: 4px;
  font-size: 12px;
}

.bm-frames {
  margin: 4px 0 0;
  white-space: pre-wrap;
  font-size: 11px;
  overflow-x: auto;
}

.bm-hidden-frames {
  opacity: 0.7;
  font-style: italic;
}

.bm-raw-stack summary {
  cursor: pointer;
  font-size: 11px;
  opacity: 0.8;
  margin-top: 4px;
}
</style>
