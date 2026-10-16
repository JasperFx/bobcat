<script setup lang="ts">
import { computed, onUnmounted, ref } from 'vue'
import {
  askRunner,
  commandProgress,
  describeTarget,
  findRunners,
  loadMode,
  loadRunner,
  modeFor,
  runnerState,
  saveMode,
  START_A_RUNNER,
  type RunnerCommandView,
  type RunnerView,
  type RunTarget,
} from '@/lib/runners'

/**
 * One run button (stoat#78): a spec, a set of failed specs, a feature, or a slice's bound specs.
 * Pressing it opens a picker of the runners that can run the target — shown with repository,
 * branch and path, because running in the wrong worktree tests the wrong code — then follows the
 * command to the run it caused. No runner is said plainly, with the command that starts one,
 * rather than a dead button.
 */
const props = withDefaults(defineProps<{ target: RunTarget; label: string; text?: boolean }>(), { text: false })

const open = ref(false)
const runners = ref<RunnerView[] | null>(null)
const failedToLoad = ref(false)
const selectedId = ref<string | undefined>(undefined)
const remembered = ref(loadMode())
const sending = ref(false)
const error = ref<string | null>(null)
const notice = ref<string | null>(null)
const asked = ref<{ runnerId: string; commandId: string } | null>(null)
const command = ref<RunnerCommandView | undefined>(undefined)

const selected = computed(() => runners.value?.find((r) => r.runnerId === selectedId.value) ?? null)
const mode = computed(() => (selected.value ? modeFor(selected.value, remembered.value) : 'cold'))
const offersWarm = computed(() => selected.value?.modes.includes('warm') ?? false)
const progress = computed(() => commandProgress(command.value))

const what = computed(() => describeTarget(props.target))

async function press(): Promise<void> {
  open.value = true
  error.value = null
  notice.value = null
  asked.value = null
  command.value = undefined
  runners.value = null
  failedToLoad.value = false

  const found = await findRunners(props.target)
  failedToLoad.value = found === null
  runners.value = found ?? []
  selectedId.value = runners.value[0]?.runnerId
}

function chooseMode(next: string | number | boolean | undefined): void {
  remembered.value = next === 'warm' ? 'warm' : 'cold'
  saveMode(remembered.value)
}

async function run(): Promise<void> {
  if (!selected.value) return
  sending.value = true
  error.value = null
  const result = await askRunner(selected.value.runnerId, { ...props.target, mode: mode.value })
  sending.value = false

  if (!result.ok) {
    error.value = result.error
    return
  }

  asked.value = { runnerId: selected.value.runnerId, commandId: result.commandId }
  follow()
}

async function forceNewRunner(): Promise<void> {
  if (!selected.value) return
  const result = await askRunner(selected.value.runnerId, { restart: true })
  notice.value = result.ok ? 'Asked for a new runner — its parent relaunches it.' : null
  error.value = result.ok ? null : result.error
}

// Following the command. The registry is not pushed, so it is polled — briefly, and only while a
// command this button sent is still on its way to a run.
const FOLLOW_MS = 1000
const FOLLOW_LIMIT_MS = 120_000
let following: ReturnType<typeof setInterval> | null = null

function follow(): void {
  stopFollowing()
  const started = Date.now()
  following = setInterval(async () => {
    if (!asked.value || Date.now() - started > FOLLOW_LIMIT_MS) return stopFollowing()
    const runner = await loadRunner(asked.value.runnerId)
    command.value = runner?.commands.find((c) => c.commandId === asked.value?.commandId)
    if (commandProgress(command.value).done) stopFollowing()
  }, FOLLOW_MS)
}

function stopFollowing(): void {
  if (following !== null) clearInterval(following)
  following = null
}

function close(): void {
  open.value = false
  stopFollowing()
}

onUnmounted(stopFollowing)

function where(runner: RunnerView): string {
  return [runner.repositorySlug ?? runner.repository, runner.branch ? `@ ${runner.branch}` : null].filter(Boolean).join(' ')
}
</script>

<template>
  <el-button size="small" :text="text" class="bm-run-button" data-testid="run-button" @click.stop="press">
    ▶ {{ label }}
  </el-button>

  <el-dialog :model-value="open" :title="`Run ${what}`" width="560px" append-to-body @close="close">
    <div v-if="runners === null" class="bm-run-muted">Looking for runners…</div>

    <div v-else-if="runners.length === 0" data-testid="no-runner">
      <p v-if="failedToLoad">The Bobcat UI could not say which runners are there.</p>
      <p v-else>No runner can run this. Start one in the checkout you want it to test:</p>
      <pre class="bm-run-command">{{ START_A_RUNNER }}</pre>
    </div>

    <template v-else>
      <el-radio-group v-model="selectedId" class="bm-run-runners" :disabled="asked !== null">
        <el-radio v-for="runner in runners" :key="runner.runnerId" :value="runner.runnerId" class="bm-run-runner" data-testid="runner-choice">
          <span class="bm-run-where">{{ where(runner) }}</span>
          <span class="bm-run-state" :data-tone="runnerState(runner).tone">{{ runnerState(runner).label }}</span>
          <!-- The path is the line under a resolved repository; without one, it already IS the name. -->
          <span class="bm-run-path" :title="runner.statusDetail ?? undefined">{{ [runner.repositorySlug ? runner.repository : null, runner.suite].filter(Boolean).join(' · ') }}</span>
        </el-radio>
      </el-radio-group>

      <div v-if="selected && offersWarm" class="bm-run-mode">
        <el-radio-group :model-value="mode" size="small" :disabled="asked !== null" @update:model-value="chooseMode">
          <el-radio-button value="cold">Cold</el-radio-button>
          <el-radio-button value="warm">Warm</el-radio-button>
        </el-radio-group>
        <span class="bm-run-muted">Cold is a fresh run of the current code; warm reuses a booted host.</span>
      </div>

      <div v-if="asked" class="bm-run-progress" data-testid="run-progress" :data-failed="progress.failed">
        {{ progress.label }}
        <router-link v-if="command?.runId" :to="`/runs/${command.runId}`" data-testid="run-link" @click="close">open the run</router-link>
      </div>
    </template>

    <el-alert v-if="error" type="error" :title="error" :closable="false" class="bm-run-alert" data-testid="run-error" />
    <el-alert v-if="notice" type="info" :title="notice" :closable="false" class="bm-run-alert" />

    <template #footer>
      <el-button v-if="selected && !asked" text data-testid="force-new-runner" @click="forceNewRunner">Force a new runner</el-button>
      <el-button @click="close">{{ asked ? 'Close' : 'Cancel' }}</el-button>
      <el-button v-if="!asked" type="primary" :disabled="!selected" :loading="sending" data-testid="run-send" @click="run">
        Run
      </el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.bm-run-runners {
  display: flex;
  flex-direction: column;
  align-items: stretch;
  gap: 6px;
}

.bm-run-runner {
  height: auto;
  margin-right: 0;
  padding: 4px 0;
  white-space: normal;
}

.bm-run-where {
  font-weight: 600;
}

.bm-run-state {
  margin-left: 8px;
  font-size: 12px;
}

.bm-run-state[data-tone='ok'] {
  color: var(--bm-state-passed);
}

.bm-run-state[data-tone='wait'] {
  color: var(--bm-state-retrying);
}

.bm-run-state[data-tone='bad'] {
  color: var(--bm-state-failed);
}

.bm-run-path {
  display: block;
  font-size: 12px;
  color: var(--bc-neutral-500);
}

.bm-run-mode {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-top: 12px;
}

.bm-run-muted {
  font-size: 12px;
  color: var(--bc-neutral-500);
}

.bm-run-command {
  padding: 8px 10px;
  border-radius: 6px;
  background: var(--bm-state-unreached);
}

.bm-run-progress {
  margin-top: 12px;
  font-size: 13px;
}

.bm-run-progress[data-failed='true'] {
  color: var(--bm-state-failed);
}

.bm-run-progress a {
  margin-left: 8px;
  color: var(--bm-link);
}

.bm-run-alert {
  margin-top: 12px;
}
</style>
