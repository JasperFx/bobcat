<script setup lang="ts">
import { computed, ref } from 'vue'
import type { RunState } from '@/stores/runs-store'
import type { RunnerView } from '@/lib/runners'
import { runnerState } from '@/lib/runners'
import { commandLineOf, plannedHintOf } from '@/lib/liveProgress'
import { useRunners } from '@/composables/useRunners'
import { storeToRefs } from 'pinia'
import { useRunnersStore } from '@/stores/runners-store'
import { buildProblemFor, buildProblems } from '@/lib/builds'
import { HISTORY_LENGTH, verdictToneOf, type SuiteGroup } from '@/lib/runBoard'
import HistoryStrip from '@/components/HistoryStrip.vue'
import LiveRunProgress from '@/components/LiveRunProgress.vue'
import RunCard from '@/components/RunCard.vue'
import RunButton from '@/components/RunButton.vue'
import { formatRelative, useNow } from '@/composables/time'

/**
 * One suite on the board (bobcat#786): its name, its repository, its latest result, a five-run
 * history strip, and — when a runner is attached — that runner's Run button in the row, where the
 * separate runner panel used to be. A live run shows its overall progress under the row whether or
 * not the row is expanded: it is the one thing on the board someone is waiting on. Expanding shows
 * the retained runs as cards, the newest few first and the rest on request.
 */
const props = defineProps<{
  group: SuiteGroup<RunState, RunnerView>
  expanded: boolean
  /** The repository's short name for the pill; null when the path is all the board knows. */
  repositoryName: string | null
}>()

const emit = defineEmits<{
  toggle: []
  filterRepository: []
  eject: [run: RunState]
  ejectOlder: [run: RunState]
  ejectOthers: [run: RunState]
}>()

const now = useNow()
const { runners } = useRunners()

const latest = computed(() => props.group.latest)
const finished = computed(() => props.group.runs.filter((run) => run.finished || run.orphaned))

/** The latest result, in words — the row's verdict when nothing is live. */
const latestVerdict = computed(() => {
  const run = latest.value
  if (!run) return null
  const tone = verdictToneOf(run)
  const age = formatRelative(run.finished ? run.finishedAt : run.startedAt, now.value)
  switch (tone) {
    case 'live':
      return { tone, text: 'running' }
    case 'orphaned':
      return { tone, text: `orphaned${age ? ` · ${age}` : ''}` }
    case 'failed':
      return { tone, text: `${run.counts?.failed ?? '?'} failed${age ? ` · ${age}` : ''}` }
    case 'retried':
      return { tone, text: `${run.counts!.passed} passed, ${run.counts!.passedOnRetry} on retry${age ? ` · ${age}` : ''}` }
    case 'passed':
      return { tone, text: `${run.counts ? `${run.counts.passed} passed` : 'passed'}${age ? ` · ${age}` : ''}` }
    default:
      return { tone, text: age ?? 'no verdict' }
  }
})

const runner = computed(() => props.group.runner)

// bobcat#795: a broken build on this suite's own row, not only in the status strip.
const { builds } = storeToRefs(useRunnersStore())
const buildProblem = computed(() =>
  buildProblemFor(buildProblems(builds.value, runners.value), props.group.suite, [
    props.group.repository,
    ...props.group.runs.map((run) => run.repository),
    props.group.runner?.repository,
  ]),
)
const runnerTone = computed(() => (runner.value ? runnerState(runner.value) : null))

// How many cards the expansion shows before "and N older": the strip's length, by default.
const showAll = ref(false)
const cards = computed(() => (showAll.value ? props.group.runs : props.group.runs.slice(0, HISTORY_LENGTH)))
const older = computed(() => props.group.runs.length - cards.value.length)

/** The suite's other, settled runs — for the planned total and the ETA; another live run says nothing yet. */
function previousTo(run: RunState): RunState[] {
  return props.group.runs.filter((other) => other.runId !== run.runId && (other.finished || other.orphaned))
}
</script>

<template>
  <section class="bm-suite" :data-live="group.live.length > 0" :data-expanded="expanded" data-testid="suite-row">
    <div class="bm-suite-row">
      <button
        type="button"
        class="bm-suite-toggle"
        :aria-expanded="expanded"
        :aria-label="`${expanded ? 'Collapse' : 'Expand'} ${group.suite}`"
        :disabled="group.runs.length === 0"
        data-testid="suite-toggle"
        @click="emit('toggle')"
      >
        {{ expanded ? '▾' : '▸' }}
      </button>
      <span class="bm-suite-name" data-testid="suite-name">{{ group.suite }}</span>
      <button
        v-if="repositoryName"
        type="button"
        class="bm-suite-repo"
        :title="`${group.repository} — filter the board to this repository`"
        :aria-label="`Filter the board to ${group.repository}`"
        data-testid="repository-pill"
        @click="emit('filterRepository')"
      >
        {{ repositoryName }}
      </button>
      <span v-else class="bm-suite-path" :title="group.repository">{{ group.repository }}</span>
      <span v-if="latestVerdict" class="bm-suite-latest" :data-tone="latestVerdict.tone" data-testid="suite-latest">
        {{ latestVerdict.text }}
      </span>
      <span v-else class="bm-suite-latest bm-suite-muted" data-testid="suite-latest">no runs yet</span>
      <span
        v-if="buildProblem"
        class="bm-suite-build"
        :data-failed="buildProblem.failed"
        :title="buildProblem.detail ?? undefined"
        data-testid="suite-build"
      >
        {{ buildProblem.failed ? 'build failed' : 'building…' }}
      </span>
      <HistoryStrip :suite="group.suite" :runs="group.runs" />
      <span v-if="runner" class="bm-suite-runner" :data-tone="runnerTone!.tone" :title="runner.statusDetail ?? undefined" data-testid="suite-runner">
        <router-link :to="`/runners/${encodeURIComponent(runner.runnerId)}`" class="bm-suite-runner-link" data-testid="runner-link">
          runner
        </router-link>
        {{ runnerTone!.label }}
      </span>
      <RunButton v-if="runner && runner.specs > 0" :target="{ all: true, runnerId: runner.runnerId }" label="Run all" :modes="runner.modes" />
    </div>

    <!-- The live run's bar sits under the row whatever the expansion: it is what someone is waiting on. -->
    <LiveRunProgress
      v-for="run in group.live"
      :key="run.runId"
      :run="run"
      :planned-hint="plannedHintOf(run, runners)"
      :command="commandLineOf(run, runners)"
      :previous="previousTo(run)"
      class="bm-suite-live"
    />

    <div v-if="expanded && group.runs.length > 0" class="bm-suite-runs" data-testid="suite-runs">
      <RunCard
        v-for="run in cards"
        :key="run.runId"
        :run="run"
        :latest="run.runId === latest?.runId"
        @eject="emit('eject', $event)"
        @eject-older="emit('ejectOlder', $event)"
        @eject-others="emit('ejectOthers', $event)"
      />
      <p v-if="older > 0" class="bm-suite-older">
        {{ older }} older run{{ older === 1 ? '' : 's' }} not shown —
        <el-button size="small" text class="bm-suite-show-all" @click="showAll = true">show all</el-button>
      </p>
      <p v-if="finished.length === 0" class="bm-suite-older">Nothing finished yet.</p>
    </div>
  </section>
</template>

<style scoped>
.bm-suite {
  padding: 6px 10px;
  margin-bottom: 6px;
  border: 1px solid var(--bc-neutral-800);
  border-radius: 6px;
}

.bm-suite[data-live='true'] {
  border-color: var(--bm-state-running);
}

.bm-suite-row {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
}

.bm-suite-toggle {
  width: 22px;
  height: 22px;
  padding: 0;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--bm-menu-text);
  cursor: pointer;
  font-size: 12px;
}

.bm-suite-toggle:hover:not(:disabled) {
  background: var(--bm-menu-hover);
  color: var(--bc-coat);
}

.bm-suite-toggle:disabled {
  opacity: 0.35;
  cursor: default;
}

.bm-suite-name {
  font-weight: 500;
  color: var(--bc-coat);
  white-space: nowrap;
}

.bm-suite-repo {
  flex: none;
  padding: 0 7px;
  border: 1px solid var(--bc-neutral-800);
  border-radius: 999px;
  background: transparent;
  font: inherit;
  font-size: 11px;
  line-height: 18px;
  color: var(--bc-neutral-300);
  cursor: pointer;
}

.bm-suite-repo:hover {
  border-color: var(--bc-accent-600);
}

.bm-suite-path {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
  color: var(--bm-menu-text);
}

.bm-suite-latest {
  flex: 1;
  min-width: 0;
  font-size: 13px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.bm-suite-latest[data-tone='passed'] {
  color: var(--bm-state-passed);
}

.bm-suite-latest[data-tone='retried'],
.bm-suite-latest[data-tone='orphaned'] {
  color: var(--bm-state-retrying);
}

.bm-suite-latest[data-tone='failed'] {
  color: var(--bm-state-failed);
}

.bm-suite-latest[data-tone='live'] {
  color: var(--bm-state-running);
}

.bm-suite-muted {
  color: var(--bm-menu-text);
}

.bm-suite-runner {
  font-size: 12px;
  white-space: nowrap;
}

.bm-suite-runner-link {
  color: var(--bm-link);
  margin-right: 4px;
}

.bm-suite-runner[data-tone='ok'] {
  color: var(--bm-state-passed);
}

.bm-suite-runner[data-tone='wait'] {
  color: var(--bm-state-retrying);
}

.bm-suite-runner[data-tone='bad'] {
  color: var(--bm-state-failed);
}

.bm-suite-live {
  margin: 8px 0 4px 34px;
}

.bm-suite-runs {
  margin: 8px 0 2px 34px;
}

.bm-suite-older {
  margin: 4px 0 0;
  font-size: 12px;
  color: var(--bm-menu-text);
}
.bm-suite-build {
  font-size: 12px;
  font-weight: 600;
  padding: 1px 8px;
  border-radius: 999px;
  color: var(--el-color-warning);
  border: 1px solid currentColor;
  white-space: nowrap;
}
.bm-suite-build[data-failed='true'] {
  color: var(--el-color-danger);
}
</style>
