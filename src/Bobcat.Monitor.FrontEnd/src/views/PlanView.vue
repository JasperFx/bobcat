<script setup lang="ts">
import { computed, onMounted, onUnmounted } from 'vue'
import { layoutDag } from '@/lib/dagLayout'
import { usePlansStore, type NodeStatus } from '@/stores/plans-store'

const props = defineProps<{ slug: string }>()
const plans = usePlansStore()

// Status changes at the observers' sweep cadence; 5s keeps the board honest without a push
// contract. Polling only lives while this view is mounted.
const POLL_MS = 5_000
let timer: ReturnType<typeof setInterval> | null = null

onMounted(() => {
  void plans.fetchPlans()
  void plans.fetchStatus(props.slug)
  timer = setInterval(() => void plans.fetchStatus(props.slug), POLL_MS)
})

onUnmounted(() => {
  if (timer !== null) clearInterval(timer)
})

const status = computed(() => plans.statusOf(props.slug))
const errors = computed(() => plans.invalid[props.slug] ?? plans.summaryOf(props.slug)?.errors ?? null)

// Fixed geometry is what lets the SVG edge layer be pure arithmetic.
const CARD_W = 240
const CARD_H = 118
const GAP_X = 72
const GAP_Y = 20

const layout = computed(() => layoutDag((status.value?.nodes ?? []).map((n) => ({ id: n.id, dependsOn: n.dependsOn }))))

const nodeById = computed(() => new Map((status.value?.nodes ?? []).map((n) => [n.id, n])))
const positions = computed(() => new Map(layout.value.nodes.map((p) => [p.id, p])))

const boardWidth = computed(() => Math.max(1, layout.value.cols * (CARD_W + GAP_X) - GAP_X))
const boardHeight = computed(() => Math.max(1, layout.value.rows * (CARD_H + GAP_Y) - GAP_Y))

function xOf(col: number): number {
  return col * (CARD_W + GAP_X)
}

function yOf(row: number): number {
  return row * (CARD_H + GAP_Y)
}

function edgePath(from: string, to: string): string {
  const a = positions.value.get(from)!
  const b = positions.value.get(to)!
  const x1 = xOf(a.col) + CARD_W
  const y1 = yOf(a.row) + CARD_H / 2
  const x2 = xOf(b.col)
  const y2 = yOf(b.row) + CARD_H / 2
  const mid = (x1 + x2) / 2
  return `M ${x1} ${y1} C ${mid} ${y1}, ${mid} ${y2}, ${x2} ${y2}`
}

/** A blocked edge — its downstream node is waiting on this dependency — renders muted;
 *  a satisfied one (upstream done) renders solid. */
function edgeDone(from: string): boolean {
  return nodeById.value.get(from)?.status === 'done'
}

function externalLink(node: NodeStatus): string | null {
  if (node.ref === null) return null
  if ((node.kind === 'issue' || node.kind === 'pr') && node.ref.includes('#')) {
    const [repo, number] = node.ref.split('#')
    return `https://github.com/${repo}/issues/${number}`
  }
  if (node.kind === 'publish' && node.ref.startsWith('nuget.org/')) {
    return `https://www.nuget.org/packages/${node.ref.slice('nuget.org/'.length)}`
  }
  return null
}
</script>

<template>
  <div>
    <h2>{{ status?.title ?? slug }}</h2>

    <el-alert v-if="errors && !status" type="error" :closable="false" title="This plan document has errors">
      <ul class="bm-plan-errors">
        <li v-for="error in errors" :key="error">{{ error }}</li>
      </ul>
    </el-alert>

    <template v-if="status">
      <div class="bm-ready-strip">
        <span class="bm-ready-label">Ready:</span>
        <template v-if="status.ready.length > 0">
          <el-tag v-for="id in status.ready" :key="id" size="small" class="bm-ready-tag">{{ id }}</el-tag>
        </template>
        <span v-else class="bm-ready-none">nothing — everything is blocked, running, or done</span>
      </div>

      <div class="bm-dag-scroll">
        <div class="bm-dag" :style="{ width: `${boardWidth}px`, height: `${boardHeight}px` }">
          <svg
            class="bm-edges"
            :width="boardWidth"
            :height="boardHeight"
            :viewBox="`0 0 ${boardWidth} ${boardHeight}`"
          >
            <path
              v-for="edge in layout.edges"
              :key="`${edge.from}->${edge.to}`"
              :d="edgePath(edge.from, edge.to)"
              class="bm-edge"
              :data-done="edgeDone(edge.from)"
            />
          </svg>

          <div
            v-for="placed in layout.nodes"
            :key="placed.id"
            class="bm-node"
            :data-status="nodeById.get(placed.id)!.status"
            :data-ready="nodeById.get(placed.id)!.ready"
            :style="{
              left: `${xOf(placed.col)}px`,
              top: `${yOf(placed.row)}px`,
              width: `${CARD_W}px`,
              height: `${CARD_H}px`,
            }"
          >
            <div class="bm-node-head">
              <span class="bm-node-kind">{{ nodeById.get(placed.id)!.kind }}</span>
              <span class="bm-node-status">{{ nodeById.get(placed.id)!.status }}</span>
              <span v-if="nodeById.get(placed.id)!.ready" class="bm-node-ready">ready</span>
            </div>
            <div class="bm-node-title" :title="nodeById.get(placed.id)!.title">
              {{ nodeById.get(placed.id)!.title }}
            </div>
            <div
              class="bm-node-detail"
              :title="nodeById.get(placed.id)!.detail ?? nodeById.get(placed.id)!.observedTitle ?? ''"
            >
              {{ nodeById.get(placed.id)!.detail ?? nodeById.get(placed.id)!.observedTitle ?? '' }}
            </div>
            <div class="bm-node-links">
              <a
                v-if="externalLink(nodeById.get(placed.id)!)"
                :href="externalLink(nodeById.get(placed.id)!)!"
                target="_blank"
                rel="noopener"
              >
                {{ nodeById.get(placed.id)!.ref }}
              </a>
              <router-link
                v-if="nodeById.get(placed.id)!.runId"
                :to="{ name: 'run', params: { runId: nodeById.get(placed.id)!.runId } }"
              >
                run ↗
              </router-link>
            </div>
          </div>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.bm-ready-strip {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  margin-bottom: 16px;
}

.bm-ready-label {
  font-size: 13px;
  color: var(--bm-menu-text);
}

.bm-ready-none {
  font-size: 13px;
  color: #909399;
}

.bm-dag-scroll {
  overflow: auto;
  padding: 8px 4px 24px;
}

.bm-dag {
  position: relative;
}

.bm-edges {
  position: absolute;
  inset: 0;
  pointer-events: none;
}

.bm-edge {
  fill: none;
  stroke: #c0c4cc;
  stroke-width: 1.5;
  stroke-dasharray: 5 4;
}

.bm-edge[data-done='true'] {
  stroke: var(--bm-state-passed);
  stroke-dasharray: none;
}

.bm-node {
  position: absolute;
  box-sizing: border-box;
  border: 1.5px solid #dcdfe6;
  border-left-width: 4px;
  border-radius: 6px;
  background: #fff;
  padding: 8px 10px;
  overflow: hidden;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.bm-node[data-ready='true'] {
  box-shadow: 0 0 0 2px var(--el-color-primary-light-7);
}

.bm-node-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
}

.bm-node-kind {
  color: #909399;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.bm-node-status {
  font-weight: 600;
}

.bm-node-ready {
  margin-left: auto;
  color: var(--bm-primary);
  font-weight: 600;
}

.bm-node-title {
  font-weight: 600;
  font-size: 13px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.bm-node-detail {
  font-size: 12px;
  color: var(--bm-menu-text);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.bm-node-links {
  margin-top: auto;
  display: flex;
  gap: 10px;
  font-size: 12px;
}

.bm-node-links a {
  color: var(--bm-primary);
  text-decoration: none;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

/* The node-state grammar, extending the run cards' running/passed/failed/retrying tokens:
   evidence states use the same colors; coordination-only states stay neutral. */
.bm-node[data-status='done'] {
  border-left-color: var(--bm-state-passed);
  background: var(--bm-state-passed-bg);
}

.bm-node[data-status='done'] .bm-node-status {
  color: var(--bm-state-passed);
}

.bm-node[data-status='failed'],
.bm-node[data-status='missing'],
.bm-node[data-status='mismatch'] {
  border-left-color: var(--bm-state-failed);
  background: var(--bm-state-failed-bg);
}

.bm-node[data-status='failed'] .bm-node-status,
.bm-node[data-status='missing'] .bm-node-status,
.bm-node[data-status='mismatch'] .bm-node-status {
  color: var(--bm-state-failed);
}

.bm-node[data-status='running'] {
  border-left-color: var(--bm-state-running);
  background: var(--bm-state-running-bg);
}

.bm-node[data-status='running'] .bm-node-status {
  color: var(--bm-state-running);
}

.bm-node[data-status='claimed'],
.bm-node[data-status='pr-open'],
.bm-node[data-status='abandoned'] {
  border-left-color: var(--bm-state-retrying);
  background: var(--bm-state-retrying-bg);
}

.bm-node[data-status='claimed'] .bm-node-status,
.bm-node[data-status='pr-open'] .bm-node-status,
.bm-node[data-status='abandoned'] .bm-node-status {
  color: var(--bm-state-retrying);
}

.bm-node[data-status='unrealized'] {
  border-style: dashed;
}

.bm-node[data-status='unknown'] {
  opacity: 0.75;
}

.bm-plan-errors {
  margin: 0;
  padding-left: 18px;
}
</style>
