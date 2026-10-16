<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import RunButton from '@/components/RunButton.vue'
import { loadRunners, runnerState, START_A_RUNNER, type RunnerView } from '@/lib/runners'

/**
 * The attached runners, on the board before anything has run (bobcat#645): each with a button that
 * runs its whole project. Without it a person who opened `bobcat ui` and started `bobcat resident`
 * had nothing to press until a `dotnet test` or `bobcat run` had made a run page to press it on.
 * The registry is not pushed, so it is polled.
 */
const props = withDefaults(defineProps<{ pollMs?: number }>(), { pollMs: 5000 })

const runners = ref<RunnerView[] | null>(null)
let timer: ReturnType<typeof setInterval> | null = null

async function refresh(): Promise<void> {
  const found = await loadRunners()
  if (found !== null) runners.value = found
  else if (runners.value === null) runners.value = []
}

onMounted(() => {
  void refresh()
  timer = setInterval(() => void refresh(), props.pollMs)
})

onUnmounted(() => {
  if (timer !== null) clearInterval(timer)
})

function where(runner: RunnerView): string {
  return [runner.repositorySlug ?? runner.repository, runner.branch ? `@ ${runner.branch}` : null].filter(Boolean).join(' ')
}
</script>

<template>
  <div v-if="runners && runners.length > 0" class="bm-runners" data-testid="runners-panel">
    <div v-for="runner in runners" :key="runner.runnerId" class="bm-runner" data-testid="runner-row">
      <router-link :to="`/runners/${encodeURIComponent(runner.runnerId)}`" class="bm-runner-name" data-testid="runner-link">
        {{ runner.suite ?? runner.runnerId }}
      </router-link>
      <span class="bm-runner-where" :title="runner.repository">{{ where(runner) }}</span>
      <span class="bm-runner-state" :data-tone="runnerState(runner).tone" :title="runner.statusDetail ?? undefined">
        {{ runnerState(runner).label }}
      </span>
      <span class="bm-runner-specs">{{ runner.specs }} spec{{ runner.specs === 1 ? '' : 's' }}</span>
      <RunButton
        v-if="runner.specs > 0"
        :target="{ all: true, runnerId: runner.runnerId }"
        label="Run all"
        data-testid="run-all"
      />
    </div>
  </div>
  <div v-else-if="runners && runners.length === 0" class="bm-runners-none" data-testid="no-runners">
    No runner attached. Start one to run specifications from here: <code>{{ START_A_RUNNER }}</code>
  </div>
</template>

<style scoped>
.bm-runners {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-bottom: 16px;
}

.bm-runner {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 10px;
  padding: 6px 10px;
  border: 1px solid var(--bm-state-unreached);
  border-radius: 6px;
}

.bm-runner-name {
  font-weight: 600;
  color: var(--bm-link);
}

.bm-runner-where,
.bm-runner-specs {
  font-size: 12px;
  color: var(--bc-neutral-500);
}

.bm-runner-state {
  font-size: 12px;
}

.bm-runner-state[data-tone='ok'] {
  color: var(--bm-state-passed);
}

.bm-runner-state[data-tone='wait'] {
  color: var(--bm-state-retrying);
}

.bm-runner-state[data-tone='bad'] {
  color: var(--bm-state-failed);
}

.bm-runners-none {
  font-size: 12px;
  color: var(--bc-neutral-500);
  margin-bottom: 16px;
}
</style>
