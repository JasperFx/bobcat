<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { storeToRefs } from 'pinia'
import RunButton from '@/components/RunButton.vue'
import { loadRunner, loadRunnerSpecs, runnerState, type RunnerSpecView, type RunnerView } from '@/lib/runners'
import { useRunnersStore } from '@/stores/runners-store'

/**
 * One attached runner and every specification it can run, with the steps each one plans (bobcat#644)
 * — a suite previewed in the UI before anything has run, as `bobcat preview` does in a terminal.
 * Every feature and every specification has its run button, and the runner its Run all.
 *
 * The page stays current (bobcat#784): the console pushes the runner list on every change, and this
 * page takes its runner from each push — connected, rebuilding, a command moving — and loads the
 * specifications again when the runner has registered again, since a rebuild may have changed them.
 * A runner id that a push no longer lists (a restarted runner's old id, kept as an alias on the
 * server) is asked for by id, as the first load does.
 */
const props = defineProps<{ runnerId: string }>()

const store = useRunnersStore()
const { runners } = storeToRefs(store)

const runner = ref<RunnerView | null | undefined>(undefined)
const specs = ref<RunnerSpecView[] | null>(null)

onMounted(async () => {
  const [found, listed] = await Promise.all([loadRunner(props.runnerId), loadRunnerSpecs(props.runnerId)])
  runner.value = found
  specs.value = listed ?? []
})

watch(runners, async (pushed) => {
  if (pushed === null) return
  const found = pushed.find((r) => r.runnerId === props.runnerId) ?? (await loadRunner(props.runnerId))
  if (!found) return
  const registeredAgain = runner.value?.registeredAt !== found.registeredAt
  runner.value = found
  if (registeredAgain) specs.value = (await loadRunnerSpecs(props.runnerId)) ?? specs.value
})

const features = computed(() => {
  const grouped = new Map<string, RunnerSpecView[]>()
  for (const spec of specs.value ?? []) {
    const list = grouped.get(spec.feature) ?? []
    list.push(spec)
    grouped.set(spec.feature, list)
  }
  return [...grouped.entries()].map(([feature, list]) => ({ feature, specs: list }))
})

const hasSteps = computed(() => (specs.value ?? []).some((s) => s.steps.length > 0))

function where(found: RunnerView): string {
  return [found.repositorySlug ?? found.repository, found.branch ? `@ ${found.branch}` : null].filter(Boolean).join(' ')
}
</script>

<template>
  <div v-if="runner === undefined" class="bm-runner-muted">Loading…</div>
  <el-empty v-else-if="runner === null" description="No such runner — it may have stopped and been replaced" />
  <div v-else data-testid="runner-page">
    <h2 class="bm-runner-heading">
      {{ runner.suite ?? runner.runnerId }}
      <RunButton
        v-if="runner.specs > 0"
        :target="{ all: true, runnerId: runner.runnerId }"
        :label="`Run all (${runner.specs})`"
      />
    </h2>
    <div class="bm-runner-meta">
      {{ where(runner) }} ·
      <span class="bm-runner-state" :data-tone="runnerState(runner).tone">{{ runnerState(runner).label }}</span>
      · {{ runner.modes.join(', ') }}
    </div>
    <pre v-if="runner.statusDetail" class="bm-runner-detail" data-testid="runner-detail">{{ runner.statusDetail }}</pre>

    <p v-if="specs && specs.length > 0 && !hasSteps" class="bm-runner-muted" data-testid="no-outline">
      This runner registered its specifications without their steps — a runner from before Bobcat could send them.
    </p>

    <section v-for="group in features" :key="group.feature" class="bm-feature" data-testid="feature">
      <h3 class="bm-feature-heading">
        {{ group.feature || '(no feature)' }}
        <RunButton v-if="group.feature" :target="{ feature: group.feature }" :label="`Run feature (${group.specs.length})`" text />
      </h3>
      <div v-for="spec in group.specs" :key="spec.identity" class="bm-spec" data-testid="spec">
        <div class="bm-spec-title">
          {{ spec.scenario }}
          <RunButton :target="{ specs: [spec.identity] }" label="Run" text />
        </div>
        <ol v-if="spec.steps.length > 0" class="bm-spec-steps">
          <li v-for="(step, index) in spec.steps" :key="index">{{ step }}</li>
        </ol>
      </div>
    </section>
  </div>
</template>

<style scoped>
.bm-runner-heading,
.bm-feature-heading,
.bm-spec-title {
  display: flex;
  align-items: center;
  gap: 12px;
}

.bm-runner-meta,
.bm-runner-muted {
  font-size: 12px;
  color: var(--bm-menu-text);
  margin-bottom: 16px;
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

.bm-runner-detail {
  padding: 8px 10px;
  border-radius: 6px;
  border-left: 3px solid var(--bm-state-failed);
  white-space: pre-wrap;
  font-size: 12px;
}

.bm-feature {
  margin-bottom: 20px;
}

.bm-spec {
  padding: 6px 0 6px 12px;
  border-left: 2px solid var(--bm-state-unreached);
  margin-bottom: 8px;
}

.bm-spec-steps {
  margin: 4px 0 0;
  padding-left: 20px;
  font-size: 13px;
}
</style>
