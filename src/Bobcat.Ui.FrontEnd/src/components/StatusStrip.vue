<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { storeToRefs } from 'pinia'
import { useRunnersStore } from '@/stores/runners-store'
import { useRunsStore, type RunState } from '@/stores/runs-store'
import { useBoardStore, UNATTRIBUTED } from '@/stores/board-store'
import { useConnectionStore } from '@/stores/connection-store'
import { buildProblems } from '@/lib/builds'
import { runnerState, START_A_RUNNER, type RunnerView } from '@/lib/runners'
import LiveRunProgress from '@/components/LiveRunProgress.vue'
import { plannedHintOf } from '@/lib/liveProgress'
import { formatRelative, useNow } from '@/composables/time'

/**
 * One line under the top navigation, on every page (bobcat#786): the attached runner, the build when
 * it is broken, the live run with a mini progress bar, `bobcat watch` when it is attached, and the
 * connection as a dot. It replaces the runner panel and build banner the board alone had, and the
 * "connected" text in the header, which sat in three different places and only on some pages. A
 * broken build turns the whole strip red — the one state a person must not miss, wherever they are.
 * Everything here is pushed to the stores (bobcat#784); nothing polls.
 */
const runnersStore = useRunnersStore()
const { runners, builds } = storeToRefs(runnersStore)
const runs = useRunsStore()
const board = useBoardStore()
const connection = useConnectionStore()
const now = useNow()

onMounted(() => {
  if (runnersStore.runners === null || runnersStore.builds === null) void runnersStore.load()
})

// ---------------------------------------------------------------- runner

function describeRunner(runner: RunnerView): string {
  const parts = [runner.suite ?? runner.runnerId, runnerState(runner).label]
  const up = formatRelative(runner.registeredAt, now.value)
  if (up && up !== 'just now') parts.push(`up ${up.replace(' ago', '')}`)
  if (runner.modes.includes('warm')) parts.push('warm')
  const built = formatRelative(runner.builtAt, now.value)
  if (built) parts.push(`built ${built}`)
  return parts.join(' · ')
}

const runnerSummary = computed(() => {
  const list = runners.value
  if (list === null) return null
  if (list.length === 0) return { tone: 'none' as const, text: 'none', runners: [] as RunnerView[] }
  const listening = list.filter((r) => r.connected).length
  const worst = list.some((r) => runnerState(r).tone === 'bad') ? 'bad' : list.some((r) => runnerState(r).tone === 'wait') ? 'wait' : 'ok'
  if (list.length === 1) return { tone: worst, text: describeRunner(list[0]!), runners: list }
  return { tone: worst, text: `${list.length} · ${listening} listening`, runners: list }
})

// ---------------------------------------------------------------- build

const problems = computed(() => buildProblems(builds.value, runners.value))
const broken = computed(() => problems.value.filter((p) => p.failed))
const buildText = computed(() => {
  if (broken.value.length === 0) return null
  return broken.value.length === 1 ? `${broken.value[0]!.suite} does not compile` : `${broken.value.length} projects do not compile`
})
const showingErrors = ref(false)

// ---------------------------------------------------------------- live run

/** The live runs the board is filtered to: the strip and the board agree about what is "here". */
const liveRuns = computed(() =>
  runs.runsNewestFirst.filter((run) => !run.finished && !run.orphaned).filter((run) => {
    const filter = board.repositoryFilter
    if (!filter) return true
    const slug = runs.repositoryOf(run)
    return filter === UNATTRIBUTED ? slug === null : slug === filter
  }),
)

const chosenLiveId = ref<string | null>(null)
const liveRun = computed<RunState | undefined>(
  () => liveRuns.value.find((run) => run.runId === chosenLiveId.value) ?? liveRuns.value[0],
)
// A chosen run that finished falls back to the newest live one.
watch(liveRuns, (list) => {
  if (chosenLiveId.value && !list.some((run) => run.runId === chosenLiveId.value)) chosenLiveId.value = null
})

/** The suite's earlier runs, for the planned total and the ETA. */
function previousOf(run: RunState): RunState[] {
  return runs.runsNewestFirst.filter((other) => other.runId !== run.runId && other.suite === run.suite && other.finished)
}

// ---------------------------------------------------------------- watch

/** A `bobcat watch` is attached when a build report has arrived; the runner's reports are its own. */
const watched = computed(() => builds.value ?? [])
const watchText = computed(() => {
  if (watched.value.length === 0) return null
  if (watched.value.length === 1) {
    const only = watched.value[0]!
    const age = formatRelative(only.at, now.value)
    return `${only.suite} · ${only.state}${age ? ` ${age}` : ''}`
  }
  return `${watched.value.length} projects`
})
const watchTitle = computed(() => watched.value.map((b) => `${b.suite}: ${b.state}`).join('\n'))
</script>

<template>
  <div class="bm-strip" :data-broken="broken.length > 0" role="status" aria-label="Bobcat status" data-testid="status-strip">
    <span class="bm-strip-segment" data-testid="strip-runner">
      <span class="bm-strip-label">Runner</span>
      <template v-if="runnerSummary === null">…</template>
      <template v-else-if="runnerSummary.tone === 'none'">
        <span class="bm-strip-muted">none — <code>{{ START_A_RUNNER }}</code></span>
      </template>
      <router-link
        v-else-if="runnerSummary.runners.length === 1"
        :to="`/runners/${encodeURIComponent(runnerSummary.runners[0]!.runnerId)}`"
        class="bm-strip-value"
        :data-tone="runnerSummary.tone"
        :title="runnerSummary.runners[0]!.statusDetail ?? undefined"
      >
        {{ runnerSummary.text }}
      </router-link>
      <el-dropdown v-else trigger="click" class="bm-strip-dropdown">
        <button type="button" class="bm-strip-value bm-strip-button" :data-tone="runnerSummary.tone" aria-label="Choose a runner">
          {{ runnerSummary.text }} ▾
        </button>
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item v-for="runner in runnerSummary.runners" :key="runner.runnerId">
              <router-link :to="`/runners/${encodeURIComponent(runner.runnerId)}`" class="bm-strip-menu-link">
                {{ describeRunner(runner) }}
              </router-link>
            </el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>
    </span>

    <span v-if="buildText" class="bm-strip-segment" data-testid="strip-build">
      <span class="bm-strip-label">Build</span>
      <button type="button" class="bm-strip-value bm-strip-button" data-tone="bad" aria-label="Show the compiler errors" @click="showingErrors = true">
        {{ buildText }}
      </button>
    </span>

    <span v-if="liveRun" class="bm-strip-segment bm-strip-live" data-testid="strip-live">
      <span class="bm-strip-label">Live</span>
      <el-dropdown v-if="liveRuns.length > 1" trigger="click" class="bm-strip-dropdown">
        <button type="button" class="bm-strip-value bm-strip-button" aria-label="Choose a live run" data-testid="strip-live-picker">
          {{ liveRun.suite }} ({{ liveRuns.length }} live) ▾
        </button>
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item v-for="run in liveRuns" :key="run.runId" data-testid="strip-live-choice" @click="chosenLiveId = run.runId">
              {{ run.suite }}<span v-if="run.branch" class="bm-strip-muted"> @ {{ run.branch }}</span>
            </el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>
      <router-link v-else :to="{ name: 'run', params: { runId: liveRun.runId } }" class="bm-strip-value" data-testid="strip-live-link">
        {{ liveRun.suite }}
      </router-link>
      <LiveRunProgress :run="liveRun" :planned-hint="plannedHintOf(liveRun, runners)" :previous="previousOf(liveRun)" compact />
    </span>

    <span v-if="watchText" class="bm-strip-segment" data-testid="strip-watch" :title="watchTitle">
      <span class="bm-strip-label">Watch</span>
      <span class="bm-strip-value">{{ watchText }}</span>
    </span>

    <span class="bm-strip-segment bm-strip-connection" data-testid="strip-connection">
      <span
        class="bm-strip-dot"
        :data-status="connection.status"
        role="img"
        :aria-label="`Connection: ${connection.status}`"
        :title="`Connection: ${connection.status}`"
      />
    </span>

    <el-dialog v-model="showingErrors" title="The build is broken" width="720px" append-to-body>
      <div v-for="problem in broken" :key="problem.suite" class="bm-strip-problem" data-testid="build-problem">
        <div class="bm-strip-problem-title">
          {{ problem.suite }} does not compile <span class="bm-strip-muted">· {{ problem.source }}</span>
        </div>
        <div class="bm-strip-muted">Its runs on the board are of the code before the change that broke the build.</div>
        <pre v-if="problem.detail" class="bm-strip-errors" data-testid="build-errors">{{ problem.detail }}</pre>
      </div>
    </el-dialog>
  </div>
</template>

<style scoped>
.bm-strip {
  display: flex;
  align-items: center;
  gap: 24px;
  min-height: 28px;
  padding: 0 20px;
  border-bottom: 1px solid var(--bm-divider);
  font-size: 12px;
  color: var(--bm-menu-text);
}

/* A broken build is the one state a person must not miss, wherever they are. */
.bm-strip[data-broken='true'] {
  background: var(--bm-state-failed-bg);
  border-bottom-color: var(--bm-state-failed);
  color: var(--bm-state-failed);
}

.bm-strip-segment {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  white-space: nowrap;
}

.bm-strip-label {
  text-transform: uppercase;
  letter-spacing: 0.06em;
  font-size: 10px;
  opacity: 0.8;
}

.bm-strip-value {
  color: var(--bc-neutral-300);
  text-decoration: none;
}

.bm-strip-value[data-tone='ok'] {
  color: var(--bm-state-passed);
}

.bm-strip-value[data-tone='wait'] {
  color: var(--bm-state-retrying);
}

.bm-strip-value[data-tone='bad'] {
  color: var(--bm-state-failed);
}

.bm-strip-button {
  padding: 0;
  border: 0;
  background: transparent;
  font: inherit;
  cursor: pointer;
}

.bm-strip-button:hover {
  text-decoration: underline;
}

.bm-strip-muted {
  opacity: 0.75;
}

.bm-strip-muted code {
  font-size: 11px;
}

.bm-strip-menu-link {
  color: inherit;
  text-decoration: none;
}

.bm-strip-live {
  flex: 1;
  min-width: 0;
}

.bm-strip-connection {
  margin-left: auto;
}

.bm-strip-dot {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--bm-state-unreached);
}

.bm-strip-dot[data-status='connected'] {
  background: var(--bm-state-passed);
}

.bm-strip-dot[data-status='reconnecting'] {
  background: var(--bm-state-retrying);
}

.bm-strip-dot[data-status='disconnected'],
.bm-strip-dot[data-status='error'] {
  background: var(--bm-state-failed);
}

.bm-strip-problem + .bm-strip-problem {
  margin-top: 16px;
}

.bm-strip-problem-title {
  font-weight: 500;
  color: var(--bm-state-failed);
}

.bm-strip-errors {
  margin: 6px 0 0;
  white-space: pre-wrap;
  font-size: 12px;
}
</style>
