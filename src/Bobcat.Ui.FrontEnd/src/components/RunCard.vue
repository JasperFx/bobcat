<script setup lang="ts">
import { computed } from 'vue'
import type { RunState } from '@/stores/runs-store'
import { useRunners } from '@/composables/useRunners'
import { rebuiltSince } from '@/lib/runners'
import { commandLineOf } from '@/lib/liveProgress'
import LaneStrip from '@/components/LaneStrip.vue'
import { formatAbsolute, formatDuration, formatRelative, useNow } from '@/composables/time'

/**
 * One run of a suite, as a row in that suite's expansion (bobcat#786) — what used to be the board's
 * card, less the suite name, which is the row above now. Its link is its age, because the age is
 * what tells one run of a suite from the next; the branch, the mode, the counts, the orphan tag and
 * the exports and ejects stay as they were.
 */
const props = defineProps<{ run: RunState; /** Whether this is the suite's newest run. */ latest: boolean }>()

const emit = defineEmits<{
  eject: [run: RunState]
  ejectOlder: [run: RunState]
  ejectOthers: [run: RunState]
}>()

const now = useNow()
const { runners } = useRunners()

/**
 * bobcat#652: the suite's latest finished run, and its runner has been rebuilt since — the board's
 * latest word on that suite is of code that has changed. Older runs are older anyway.
 */
const stale = computed(() => props.latest && props.run.finished ? rebuiltSince(props.run, runners.value) : undefined)

/** What a commanded run was asked to run (bobcat#646), and how far through its selection it is. */
const commandLine = computed(() => commandLineOf(props.run, runners.value))

type ExportFormat = 'ctrf' | 'junit' | 'ndjson'

const EXPORTS: { format: ExportFormat; label: string }[] = [
  { format: 'ctrf', label: 'Export CTRF' },
  { format: 'junit', label: 'Export JUnit' },
  { format: 'ndjson', label: 'Export NDJSON' },
]

function exportUrl(format: ExportFormat): string {
  return `/api/runs/${props.run.runId}/export?format=${format}`
}

/**
 * The card's age (issue #196): a finished run is aged from when it finished and a live one from when
 * it started, and the label says which — the same words mean two different things otherwise.
 */
const age = computed(() => {
  const relative = formatRelative(props.run.finished ? props.run.finishedAt : props.run.startedAt, now.value)
  if (relative === null) return 'run'
  return props.run.finished ? relative : `started ${relative}`
})

const ageTitle = computed(() => {
  const started = formatAbsolute(props.run.startedAt)
  const finished = formatAbsolute(props.run.finishedAt)
  return finished ? `started ${started}\nfinished ${finished}` : `started ${started}`
})

const duration = computed(() => (props.run.finished ? formatDuration(props.run.startedAt, props.run.finishedAt) : null))

/** Branch and mode. The repository is the row's, so the card need not repeat it. */
const meta = computed(() => [props.run.branch ? `@ ${props.run.branch}` : null, props.run.mode].filter(Boolean).join(' · '))

/**
 * A run is ejectable once nothing is publishing to it (bobcat#784: orphaned counts). A live run's
 * next event recreates the entry server-side, so offering to eject one would promise something the
 * registry undoes.
 */
const ejectable = computed(() => props.run.finished || props.run.orphaned)
const live = computed(() => !props.run.finished && !props.run.orphaned)
</script>

<template>
  <el-card class="bm-run-card" :data-live="live" data-testid="run-card">
    <div class="bm-run-row">
      <router-link
        :to="{ name: 'run', params: { runId: run.runId } }"
        class="bm-run-title"
        :title="ageTitle"
        :aria-label="`Open the run of ${run.suite} ${age}`"
        data-testid="run-link"
      >
        {{ age }}
      </router-link>
      <span class="bm-run-meta" :title="run.repository">{{ meta }}</span>
      <span v-if="commandLine" class="bm-run-command" data-testid="run-command">{{ commandLine }}</span>
      <el-tag
        v-if="stale"
        size="small"
        type="warning"
        data-testid="before-rebuild"
        :title="`The runner was rebuilt at ${stale.builtAt} and has not run this since`"
      >
        from before the last rebuild
      </el-tag>
      <span v-if="run.counts" class="bm-run-counts">
        <span class="bm-passed">{{ run.counts.passed }} passed</span>
        <span v-if="run.counts.passedOnRetry > 0" class="bm-retried">{{ run.counts.passedOnRetry }} on retry</span>
        <span v-if="run.counts.failed > 0" class="bm-failed">{{ run.counts.failed }} failed</span>
        <span v-if="run.counts.indeterminate > 0" class="bm-failed">{{ run.counts.indeterminate }} indeterminate</span>
        <span v-if="(run.counts.skipped ?? 0) > 0" class="bm-skipped">{{ run.counts.skipped }} skipped</span>
      </span>
      <el-tag v-if="run.orphaned" type="warning" size="small" title="The publisher went away and the run never finished.">
        orphaned
      </el-tag>
      <span v-if="duration" class="bm-run-duration">took {{ duration }}</span>
      <!-- Finished or orphaned (bobcat#784): nothing is publishing to either, so both can go. -->
      <template v-if="ejectable">
        <el-button size="small" text class="bm-eject" @click="emit('eject', run)">Eject</el-button>
        <el-dropdown trigger="click" class="bm-run-menu">
          <el-button size="small" text class="bm-run-menu-button" aria-label="More actions for this run">⋯</el-button>
          <template #dropdown>
            <el-dropdown-menu>
              <!-- Exporting is rare (stoat#75), so it sits in this menu beside the bulk ejects rather
                   than as three links on every card — still real links, the same URLs. -->
              <el-dropdown-item v-for="item in EXPORTS" :key="item.format">
                <a :href="exportUrl(item.format)" download class="bm-export">{{ item.label }}</a>
              </el-dropdown-item>
              <el-dropdown-item divided @click="emit('ejectOlder', run)">Eject all older</el-dropdown-item>
              <el-dropdown-item @click="emit('ejectOthers', run)">Eject all but this</el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
      </template>
    </div>
    <LaneStrip :run="run" />
  </el-card>
</template>

<style scoped>
/* One line per run (stoat#75): the board is for scanning, so a card is a row, not a panel. */
.bm-run-card {
  margin-bottom: 4px;
}

.bm-run-card :deep(.el-card__body) {
  padding: 6px 14px;
}

.bm-run-row {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
}

.bm-run-title {
  font-weight: 500;
  color: var(--bm-primary);
  text-decoration: none;
  white-space: nowrap;
}

.bm-run-meta {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
  color: var(--bm-menu-text);
}

.bm-run-counts {
  display: flex;
  gap: 10px;
  font-size: 13px;
  white-space: nowrap;
}

.bm-run-duration,
.bm-run-command {
  font-size: 12px;
  color: var(--bm-menu-text);
  white-space: nowrap;
}

.bm-passed {
  color: var(--bm-state-passed);
}

.bm-retried {
  color: var(--bm-state-retrying);
}

.bm-failed {
  color: var(--bm-state-failed);
}

.bm-skipped {
  color: var(--bm-state-unreached);
}

.bm-eject,
.bm-run-menu-button {
  margin-left: 0;
}

.bm-run-menu-button {
  font-size: 16px;
  line-height: 1;
}

/* The whole menu row is the link, not just its words. */
.bm-export {
  display: block;
  margin: 0 -16px;
  padding: 0 16px;
  color: inherit;
  text-decoration: none;
}
</style>
