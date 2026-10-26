<script setup lang="ts">
import { computed } from 'vue'
import type { RunState } from '@/stores/runs-store'
import { HISTORY_LENGTH, verdictToneOf } from '@/lib/runBoard'
import { formatAbsolute, formatRelative, useNow } from '@/composables/time'

/**
 * A suite's last few runs as a strip of segments (bobcat#786), oldest at the left and newest at the
 * right, the way a CI history reads. Each segment is a real link to its run, labelled for a screen
 * reader with the verdict, the age and the branch — the same words its tooltip shows — so the strip
 * is five links, not five coloured boxes.
 */
const props = defineProps<{ suite: string; runs: readonly RunState[] }>()

const now = useNow()

/** Newest first in, oldest first out: the newest run is the rightmost segment. */
const segments = computed(() => [...props.runs].slice(0, HISTORY_LENGTH).reverse())

function verdictOf(run: RunState): string {
  switch (verdictToneOf(run)) {
    case 'live':
      return 'running'
    case 'orphaned':
      return 'orphaned'
    case 'failed':
      return `failed (${run.counts?.failed ?? '?'} failed)`
    case 'retried':
      return `passed, ${run.counts!.passedOnRetry} on retry`
    case 'passed':
      return 'passed'
    default:
      return 'no verdict'
  }
}

function labelOf(run: RunState): string {
  const age = formatRelative(run.finished ? run.finishedAt : run.startedAt, now.value)
  const branch = run.branch ? `branch ${run.branch}` : 'no branch'
  return [`${props.suite}: ${verdictOf(run)}`, age, branch].filter(Boolean).join(', ')
}

function titleOf(run: RunState): string {
  return `${labelOf(run)}\nstarted ${formatAbsolute(run.startedAt) ?? '?'}`
}
</script>

<template>
  <span v-if="segments.length > 0" class="bm-history" data-testid="history-strip" role="list" :aria-label="`Recent runs of ${suite}`">
    <router-link
      v-for="run in segments"
      :key="run.runId"
      :to="{ name: 'run', params: { runId: run.runId } }"
      class="bm-history-segment"
      :data-tone="verdictToneOf(run)"
      :aria-label="labelOf(run)"
      :title="titleOf(run)"
      role="listitem"
      data-testid="history-segment"
    />
  </span>
</template>

<style scoped>
.bm-history {
  display: inline-flex;
  gap: 3px;
  align-items: center;
}

/* Taller than wide, like a CI history strip; the newest is rightmost. */
.bm-history-segment {
  display: inline-block;
  width: 8px;
  height: 16px;
  border-radius: 2px;
  background: var(--bm-state-unreached);
}

.bm-history-segment:hover {
  outline: 1px solid var(--bc-coat);
}

.bm-history-segment[data-tone='passed'] {
  background: var(--bm-state-passed);
}

.bm-history-segment[data-tone='retried'] {
  background: var(--bm-state-retrying);
}

.bm-history-segment[data-tone='failed'] {
  background: var(--bm-state-failed);
}

.bm-history-segment[data-tone='live'] {
  background: var(--bm-state-running);
}

.bm-history-segment[data-tone='orphaned'] {
  background: var(--bm-state-retrying-bg);
  border: 1px dashed var(--bm-state-retrying);
  box-sizing: border-box;
}
</style>
