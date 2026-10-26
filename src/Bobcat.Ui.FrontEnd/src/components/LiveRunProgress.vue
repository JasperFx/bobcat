<script setup lang="ts">
import { computed } from 'vue'
import type { RunState } from '@/stores/runs-store'
import { formatElapsed, formatEta, liveProgressOf } from '@/lib/liveProgress'
import { useNow } from '@/composables/time'

/**
 * A live run's overall progress bar (bobcat#786): tests done of the planned total, passed and failed
 * so far, elapsed time and an ETA from the suite's previous runs — green while everything passes,
 * red at the first failure, amber when only retries have happened. The same computation
 * (`liveProgressOf`) draws the status strip's mini bar, so `compact` is a rendering choice and not a
 * second opinion. Stop is deliberately absent until the issue that wires it lands; a button that
 * does nothing is worse than none.
 */
const props = withDefaults(
  defineProps<{
    run: RunState
    /** The runner's word on the total (`plannedHintOf`), a fallback for the planned total. */
    plannedHint?: number | null
    /** What a commanded run was asked to run (`commandLineOf`); null for a run nobody commanded. */
    command?: string | null
    /** The suite's earlier runs, for the planned total and the ETA. */
    previous?: readonly RunState[]
    /** The strip's one-line form: the bar, done/planned and the tone. */
    compact?: boolean
  }>(),
  { plannedHint: null, command: null, previous: () => [], compact: false },
)

// The elapsed clock ticks every second; the age labels elsewhere can afford fifteen.
const now = useNow(1_000)

const progress = computed(() => liveProgressOf(props.run, now.value, props.plannedHint, props.previous))
const percent = computed(() => (progress.value.fraction === null ? null : Math.round(progress.value.fraction * 100)))
const eta = computed(() => formatEta(progress.value.etaMs))

const summary = computed(() => {
  const { counts, planned } = progress.value
  const done = planned === null ? `${counts.done} done` : `${counts.done} / ${planned}`
  const parts = [done, `${counts.passed} passed`]
  if (counts.failed > 0) parts.push(`${counts.failed} failed`)
  if (counts.retried > 0) parts.push(`${counts.retried} retried`)
  return parts.join(' · ')
})

const label = computed(() => `${props.run.suite}: ${summary.value}, ${formatElapsed(progress.value.elapsedMs)} elapsed`)
</script>

<template>
  <div class="bm-live" :class="{ 'is-compact': compact }" :data-tone="progress.tone" data-testid="live-progress">
    <div
      class="bm-live-bar"
      role="progressbar"
      :aria-label="label"
      :aria-valuemin="0"
      :aria-valuemax="100"
      :aria-valuenow="percent ?? undefined"
      :data-indeterminate="percent === null"
    >
      <div class="bm-live-fill" :style="percent === null ? undefined : { width: `${percent}%` }" />
    </div>
    <div class="bm-live-text">
      <span class="bm-live-counts" data-testid="live-counts">{{ summary }}</span>
      <template v-if="!compact">
        <span v-if="command" class="bm-live-command" data-testid="run-command">{{ command }}</span>
        <span class="bm-live-clock" data-testid="live-elapsed">{{ formatElapsed(progress.elapsedMs) }} elapsed</span>
        <span v-if="eta" class="bm-live-clock" data-testid="live-eta">{{ eta }} left</span>
        <span v-else class="bm-live-clock bm-live-muted" data-testid="live-eta">no estimate yet</span>
        <router-link
          :to="{ name: 'run', params: { runId: run.runId }, hash: '#running' }"
          class="bm-live-go"
          data-testid="go-to-running"
        >
          Go to running
        </router-link>
        <!-- Stop is wired by a later issue; until then the button is not drawn at all. -->
      </template>
    </div>
  </div>
</template>

<style scoped>
.bm-live {
  display: flex;
  flex-direction: column;
  gap: 4px;
  --bm-live-colour: var(--bm-state-passed);
}

.bm-live[data-tone='amber'] {
  --bm-live-colour: var(--bm-state-retrying);
}

.bm-live[data-tone='red'] {
  --bm-live-colour: var(--bm-state-failed);
}

.bm-live-bar {
  position: relative;
  height: 6px;
  overflow: hidden;
  border-radius: 3px;
  background: var(--bc-neutral-800);
}

.bm-live-fill {
  height: 100%;
  width: 0;
  border-radius: 3px;
  background: var(--bm-live-colour);
  transition: width 0.4s ease;
}

/* No total to measure against: a sweep, so the bar still says "alive" without claiming a position. */
.bm-live-bar[data-indeterminate='true'] .bm-live-fill {
  width: 30%;
  animation: bm-live-sweep 1.6s ease-in-out infinite;
}

@keyframes bm-live-sweep {
  from {
    transform: translateX(-100%);
  }
  to {
    transform: translateX(340%);
  }
}

.bm-live-text {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: baseline;
  font-size: 12px;
  color: var(--bm-menu-text);
}

.bm-live-counts {
  color: var(--bm-live-colour);
  font-variant-numeric: tabular-nums;
}

.bm-live-clock {
  font-variant-numeric: tabular-nums;
}

.bm-live-command {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.bm-live-muted {
  opacity: 0.7;
}

.bm-live-go {
  margin-left: auto;
  color: var(--bm-link);
}

/* The strip's form: one line, the bar narrow beside its numbers. */
.bm-live.is-compact {
  flex-direction: row;
  align-items: center;
  gap: 8px;
}

.bm-live.is-compact .bm-live-bar {
  width: 96px;
  height: 5px;
}

.bm-live.is-compact .bm-live-text {
  gap: 6px;
}
</style>
