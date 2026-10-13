<script setup lang="ts">
import { useRunsStore, type LaneState, type RunState } from '@/stores/runs-store'

/**
 * The run card's one-line view of a supervised run's topology (issue #84): a chip per lane
 * colored by what its worker is doing, naming the scenario it is on, plus recycle and
 * worker-fault counts. Renders nothing for an in-process run, which has no lanes.
 */
const props = defineProps<{ run: RunState }>()

const runs = useRunsStore()

function current(lane: LaneState): string | null {
  const running = runs.runningIn(props.run, lane)
  if (running.length === 0) return null
  const first = running[0]!
  const name = first.scenario || first.uid
  return running.length > 1 ? `${name} +${running.length - 1}` : name
}

function laneTitle(lane: LaneState): string {
  const pass = lane.passes > 1 ? `, pass ${lane.passes}` : ''
  const handed = `${lane.uids.length} test${lane.uids.length === 1 ? '' : 's'} handed`
  return `lane ${lane.lane}: ${lane.status}${pass} — ${handed}`
}
</script>

<template>
  <div v-if="runs.hasTopology(run)" class="bm-lane-strip" data-testid="lane-strip">
    <span
      v-for="lane in run.lanes"
      :key="lane.lane"
      class="bm-lane-chip"
      :data-status="lane.status"
      :data-lane="lane.lane"
      :title="laneTitle(lane)"
    >
      lane {{ lane.lane }}<span v-if="lane.passes > 1" class="bm-lane-pass">×{{ lane.passes }}</span>
      <span v-if="current(lane)" class="bm-lane-current"> {{ current(lane) }}</span>
      <span v-else-if="lane.status === 'crashed'" class="bm-lane-current"> crashed</span>
      <span v-else-if="lane.status === 'finished'" class="bm-lane-current"> done</span>
    </span>
    <span v-if="run.recycles.length > 0" class="bm-recycles" data-testid="recycle-count">
      {{ run.recycles.length }} recycle{{ run.recycles.length === 1 ? '' : 's' }}
    </span>
    <span v-if="run.faults.length > 0" class="bm-faults" data-testid="fault-count">
      {{ run.faults.length }} worker fault{{ run.faults.length === 1 ? '' : 's' }}
    </span>
  </div>
</template>

<style scoped>
.bm-lane-strip {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin: 6px 0;
  font-size: 12px;
}

.bm-lane-chip {
  border-left: 3px solid var(--bm-state-running);
  background: var(--bm-state-running-bg);
  border-radius: 3px;
  padding: 1px 8px;
  color: var(--bm-menu-text);
  white-space: nowrap;
}

.bm-lane-chip[data-status='finished'] {
  border-color: var(--bm-state-passed);
  background: var(--bm-state-passed-bg);
}

.bm-lane-chip[data-status='crashed'] {
  border-color: var(--bm-state-failed);
  background: var(--bm-state-failed-bg);
  color: var(--bm-state-failed);
}

.bm-lane-pass {
  color: var(--bm-state-retrying);
  margin-left: 2px;
}

.bm-lane-current {
  font-style: italic;
}

.bm-recycles {
  color: var(--bm-state-retrying);
}

.bm-faults {
  color: var(--bm-state-failed);
  font-weight: 600;
}
</style>
