<script setup lang="ts">
import { useRunsStore, type LaneState, type RunState } from '@/stores/runs-store'

/**
 * The run detail's view of a supervised run's topology (issue #84): every lane with what it
 * was handed and what it is on now, the recycle timeline, and each worker death with the exit
 * code and last standard error the supervisor captured. Renders nothing for an in-process run.
 */
const props = defineProps<{ run: RunState }>()

const runs = useRunsStore()

function runningNames(lane: LaneState): string {
  return runs
    .runningIn(props.run, lane)
    .map((s) => s.scenario || s.uid)
    .join(', ')
}

function clock(at: string): string {
  const date = new Date(at)
  return Number.isNaN(date.getTime()) ? at : date.toLocaleTimeString()
}
</script>

<template>
  <section v-if="runs.hasTopology(run)" class="bm-topology" data-testid="supervisor-topology">
    <h3>Workers</h3>

    <table v-if="run.lanes.length > 0" class="bm-lanes">
      <thead>
        <tr>
          <th>Lane</th>
          <th>Status</th>
          <th>Pass</th>
          <th>Handed</th>
          <th>Running now</th>
          <th>Outcomes</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="lane in run.lanes" :key="lane.lane" :data-status="lane.status" :data-lane="lane.lane">
          <td>lane {{ lane.lane }}</td>
          <td class="bm-lane-status">{{ lane.status }}</td>
          <td>{{ lane.passes }}</td>
          <td :title="lane.uids.join('\n')">{{ lane.uids.length }}</td>
          <td class="bm-lane-running">{{ runningNames(lane) || '—' }}</td>
          <td>{{ lane.outcomes ?? '—' }}</td>
        </tr>
      </tbody>
    </table>

    <div v-if="run.recycles.length > 0" class="bm-recycles" data-testid="recycles">
      <h4>Recycled</h4>
      <ul>
        <li v-for="(recycle, i) in run.recycles" :key="i">
          <span class="bm-clock">{{ clock(recycle.at) }}</span> {{ recycle.resource }}
        </li>
      </ul>
    </div>

    <div v-if="run.faults.length > 0" class="bm-faults" data-testid="worker-faults">
      <h4>Worker faults</h4>
      <div v-for="(fault, i) in run.faults" :key="i" class="bm-fault">
        <div class="bm-fault-head">
          <span class="bm-clock">{{ clock(fault.at) }}</span>
          <span v-if="fault.lane !== null" class="bm-fault-lane">lane {{ fault.lane }}</span>
          <span v-else class="bm-fault-lane">isolated process</span>
          <span v-if="fault.exitCode !== null" class="bm-fault-exit">exit code {{ fault.exitCode }}</span>
          <span v-else class="bm-fault-exit">still running</span>
        </div>
        <div class="bm-fault-text">{{ fault.fault }}</div>
        <pre v-if="fault.standardError" class="bm-fault-stderr">{{ fault.standardError }}</pre>
      </div>
    </div>
  </section>
</template>

<style scoped>
.bm-topology {
  margin: 0 0 16px;
  font-size: 13px;
}

.bm-topology h3 {
  margin: 0 0 6px;
  font-size: 14px;
}

.bm-topology h4 {
  margin: 10px 0 4px;
  font-size: 13px;
  color: var(--bm-menu-text);
}

.bm-lanes {
  border-collapse: collapse;
}

.bm-lanes th,
.bm-lanes td {
  text-align: left;
  padding: 2px 12px 2px 0;
}

.bm-lanes th {
  font-weight: 600;
  color: var(--bm-menu-text);
}

.bm-lanes tr[data-status='running'] .bm-lane-status {
  color: var(--bm-state-running);
}

.bm-lanes tr[data-status='finished'] .bm-lane-status {
  color: var(--bm-state-passed);
}

.bm-lanes tr[data-status='crashed'] .bm-lane-status {
  color: var(--bm-state-failed);
  font-weight: 600;
}

.bm-lane-running {
  font-style: italic;
}

.bm-recycles ul {
  margin: 0;
  padding-left: 16px;
}

.bm-clock {
  color: var(--bm-menu-text);
  font-size: 12px;
  margin-right: 6px;
}

.bm-fault {
  border-left: 3px solid var(--bm-state-failed);
  background: var(--bm-state-failed-bg);
  padding: 6px 10px;
  margin-bottom: 8px;
}

.bm-fault-head {
  display: flex;
  gap: 10px;
  align-items: baseline;
}

.bm-fault-lane,
.bm-fault-exit {
  font-weight: 600;
  color: var(--bm-state-failed);
}

.bm-fault-text {
  margin-top: 2px;
}

.bm-fault-stderr {
  margin: 6px 0 0;
  font-size: 12px;
  white-space: pre-wrap;
  max-height: 200px;
  overflow: auto;
}
</style>
