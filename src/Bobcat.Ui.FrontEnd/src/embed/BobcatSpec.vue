<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import ResultsScenarioBody from '@/results/ResultsScenarioBody.vue'
import { pageDataOf, scenarioNamed, type ResultsPageData, type ResultsScenarioView } from '@/results/fromResults'

/**
 * `<bobcat-spec src="results.json" spec="Feature/Scenario">` (bobcat#553): one specification from a
 * results file, rendered through the board's own `SpecificationView` on a documentation page.
 *
 * `src` is the results JSON — `bobcat results-docs` writes it as `results.json` in the page's
 * shape, and a run's own results file reads too — and it is the only thing the element fetches.
 * `spec` is the identity, `{Feature}/{Scenario}`. Everything else about the element is the shadow
 * root `defineCustomElement` gives it: the board's styles ride inside it and the host page's do not
 * reach in, so one script tag and one element are the whole integration.
 */
const props = defineProps<{ src: string; spec: string }>()

const data = ref<ResultsPageData | null>(null)
const error = ref<string | null>(null)

/** The page can swap the fetch for a test, or for results it already holds. */
type Fetcher = (src: string) => Promise<unknown>
const defaultFetcher: Fetcher = async (src) => {
  const response = await fetch(src)
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}`.trim())
  return response.json()
}

async function load(src: string) {
  data.value = null
  error.value = null
  if (!src) {
    error.value = 'bobcat-spec needs a src: the results.json `bobcat results-docs` wrote, or a run\'s results file.'
    return
  }
  try {
    const fetcher = (globalThis as { bobcatSpecFetch?: Fetcher }).bobcatSpecFetch ?? defaultFetcher
    const loaded = pageDataOf(await fetcher(src))
    data.value = loaded
  } catch (e) {
    error.value = `Could not read ${src}: ${(e as Error).message}`
  }
}

watch(() => props.src, load, { immediate: true })

const scenario = computed<ResultsScenarioView | null>(() => (data.value && props.spec ? scenarioNamed(data.value, props.spec) : null))

const GLYPHS: Record<string, string> = { passed: '✓', failed: '✗', 'acceptance-failing': '◌' }

function seconds(ms: number | null): string {
  if (ms === null) return ''
  return ms < 1000 ? `${ms}ms` : `${(ms / 1000).toFixed(1)}s`
}
</script>

<template>
  <div class="bs-root" :data-status="scenario?.status ?? 'none'" data-testid="bobcat-spec">
    <p v-if="error" class="bs-message" data-testid="spec-error">{{ error }}</p>
    <p v-else-if="data === null" class="bs-message bs-loading" data-testid="spec-loading">Loading {{ src }}…</p>
    <p v-else-if="!spec" class="bs-message" data-testid="spec-error">bobcat-spec needs a spec: the specification's identity, Feature/Scenario.</p>
    <p v-else-if="scenario === null" class="bs-message" data-testid="spec-missing">No specification named “{{ spec }}” in {{ src }}.</p>
    <article v-else class="bs-scenario" data-testid="spec-scenario">
      <header class="bs-head">
        <span class="bs-glyph" aria-hidden="true">{{ GLYPHS[scenario.status] ?? '•' }}</span>
        <strong class="bs-title">{{ scenario.title }}</strong>
        <span class="bs-verdict">{{ scenario.verdict }}</span>
        <span class="bs-duration">{{ seconds(scenario.durationMs) }}</span>
      </header>
      <ResultsScenarioBody :scenario="scenario" />
    </article>
  </div>
</template>

<style>
.bs-root {
  display: block;
  background: var(--bc-ground);
  color: var(--bc-neutral-200);
  font-family: system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif;
  font-size: 14px;
  line-height: 1.45;
  border-left: 3px solid var(--bm-state-passed);
  border-radius: 0 4px 4px 0;
  padding: 8px 12px;
  margin: 8px 0;
  text-align: left;
}

.bs-root[data-status='failed'] {
  border-left-color: var(--bm-state-failed);
}

.bs-root[data-status='acceptance-failing'] {
  border-left-color: var(--bm-state-retrying);
}

.bs-root[data-status='none'] {
  border-left-color: var(--bm-divider);
}

.bs-message {
  margin: 0;
  font-size: 13px;
  color: var(--bm-state-failed);
}

.bs-loading {
  color: var(--bm-menu-text);
}

.bs-head {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 8px;
  margin-bottom: 2px;
}

.bs-glyph {
  display: inline-block;
  width: 1em;
}

.bs-root[data-status='passed'] .bs-glyph {
  color: var(--bm-state-passed);
}

.bs-root[data-status='failed'] .bs-glyph {
  color: var(--bm-state-failed);
}

.bs-root[data-status='acceptance-failing'] .bs-glyph {
  color: var(--bm-state-retrying);
}

.bs-title {
  color: var(--bc-coat);
  font-weight: 500;
}

.bs-verdict,
.bs-duration {
  font-size: 12px;
  color: var(--bm-menu-text);
}
</style>
