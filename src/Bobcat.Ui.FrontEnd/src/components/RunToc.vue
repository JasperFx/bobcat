<script setup lang="ts">
import { computed } from 'vue'
import { filterTree, flattenTree, TOC_STATUSES, type RunTree, type TocStatus } from '@/lib/runTree'

/**
 * The run page's table-of-contents rail (bobcat#787): the suite tree — suite › feature › scenario —
 * with a live status on every node and rollup counts per feature. It filters by status, a click on
 * a node jumps to it on the page, "Go to running" jumps to the scenario in flight, and "follow"
 * keeps that one in view as the run advances. A toggle to the slice tree comes with the Specs
 * explorer, later.
 *
 * bobcat#801: a tree of any depth, each node foldable (▸/▾) and zoomable (⤢), with fold-all and
 * unfold-all beside the other tools. The page owns the fold state and the zoom, so the rail and the
 * page body always agree about what is open.
 */
const props = defineProps<{
  suite: string
  /** The tree as the page shows it: the whole run, or the node it is zoomed into. */
  tree: RunTree
  filter: TocStatus | null
  /** The anchors of the nodes folded right now. */
  collapsed: ReadonlySet<string>
  /** The node the page is zoomed into, or null. */
  zoom: string | null
  /** The uid of the scenario in flight, or null. */
  running: string | null
  follow: boolean
}>()

const emit = defineEmits<{
  (e: 'update:filter', value: TocStatus | null): void
  (e: 'update:follow', value: boolean): void
  (e: 'jump', anchor: string): void
  (e: 'toggle', anchor: string): void
  (e: 'zoom', anchor: string | null): void
  (e: 'collapse-all'): void
  (e: 'expand-all'): void
  (e: 'go-to-running'): void
}>()

const LABELS: Record<TocStatus, string> = {
  running: 'running',
  failed: 'failed',
  pending: 'no result yet',
  passed: 'passed',
}

const GLYPHS: Record<TocStatus, string> = {
  running: '▶',
  failed: '✗',
  pending: '○',
  passed: '✓',
}

const shown = computed(() => filterTree(props.tree, props.filter))

/** The nodes as lines, parents first, a folded node's descendants and scenarios left out. */
const rows = computed(() => flattenTree(shown.value.features, (node) => props.collapsed.has(node.anchor)))

/** Fold-all and unfold-all are offered only when there is more than one node to fold. */
const foldable = computed(() => rows.value.length > 1 || props.collapsed.size > 0)

/** Only the statuses the run has anything of are offered — a chip for nothing is a dead control. */
const choices = computed(() => TOC_STATUSES.filter((status) => props.tree.counts[status] > 0))

function toggle(status: TocStatus): void {
  emit('update:filter', props.filter === status ? null : status)
}

function label(status: TocStatus): string {
  return LABELS[status]
}

function glyph(status: TocStatus): string {
  return GLYPHS[status]
}

/** `3 failed · 12 passed`: the feature's rollup, in the order a reader triages. */
function rollupOf(counts: RunTree['counts']): string {
  return TOC_STATUSES.filter((status) => counts[status] > 0)
    .map((status) => `${counts[status]} ${LABELS[status]}`)
    .join(' · ')
}
</script>

<template>
  <nav class="bm-toc" aria-label="Table of contents" data-testid="run-toc">
    <div class="bm-toc-tools">
      <el-button
        size="small"
        :disabled="running === null"
        data-testid="go-to-running"
        title="Scroll the running scenario into view"
        @click="emit('go-to-running')"
      >
        Go to running
      </el-button>
      <label class="bm-toc-follow" title="Keep the running scenario in view as the run advances">
        <el-switch
          size="small"
          :model-value="follow"
          data-testid="follow"
          @update:model-value="(value: string | number | boolean) => emit('update:follow', value === true)"
        />
        follow
      </label>
      <span v-if="foldable" class="bm-toc-fold-all">
        <button type="button" class="bm-toc-icon" title="Fold every suite" aria-label="Fold every suite" data-testid="collapse-all" @click="emit('collapse-all')">⊟</button>
        <button type="button" class="bm-toc-icon" title="Unfold every suite" aria-label="Unfold every suite" data-testid="expand-all" @click="emit('expand-all')">⊞</button>
      </span>
    </div>

    <div class="bm-toc-filter" role="group" aria-label="Filter by status">
      <button
        v-for="status in choices"
        :key="status"
        type="button"
        class="bm-toc-chip"
        :data-status="status"
        :data-active="filter === status ? 'true' : undefined"
        :aria-pressed="filter === status"
        :data-testid="`filter-${status}`"
        @click="toggle(status)"
      >
        {{ glyph(status) }} {{ tree.counts[status] }} {{ label(status) }}
      </button>
    </div>

    <div class="bm-toc-suite" :data-status="tree.status" data-testid="toc-suite">
      <span class="bm-toc-glyph">{{ glyph(tree.status) }}</span>
      <span class="bm-toc-name">{{ suite }}</span>
      <button v-if="zoom" type="button" class="bm-toc-unzoom" title="Show the whole run" data-testid="toc-unzoom" @click="emit('zoom', null)">whole run</button>
    </div>

    <ul class="bm-toc-features">
      <li
        v-for="row in rows"
        :key="row.node.anchor"
        class="bm-toc-feature"
        :style="{ '--bm-depth': row.depth }"
        :data-status="row.node.status"
        :data-anchor="row.node.anchor"
        :data-folded="row.collapsed ? 'true' : undefined"
        data-testid="toc-feature"
      >
        <span class="bm-toc-twisty">
          <button
            type="button"
            class="bm-toc-icon"
            :aria-expanded="!row.collapsed"
            :title="row.collapsed ? 'Unfold' : 'Fold'"
            data-testid="toc-toggle"
            @click="emit('toggle', row.node.anchor)"
          >
            {{ row.collapsed ? '▸' : '▾' }}
          </button>
        </span>
        <button type="button" class="bm-toc-node bm-toc-feature-node" :title="rollupOf(row.node.counts)" @click="emit('jump', row.node.anchor)">
          <span class="bm-toc-glyph">{{ glyph(row.node.status) }}</span>
          <span class="bm-toc-name">{{ row.node.name || '(no feature)' }}</span>
          <span class="bm-toc-rollup" data-testid="toc-rollup">
            <span v-for="status in TOC_STATUSES.filter((s) => row.node.counts[s] > 0)" :key="status" class="bm-toc-count" :data-status="status">
              {{ row.node.counts[status] }}
            </span>
          </span>
        </button>
        <span v-if="row.node.anchor !== zoom" class="bm-toc-zoom">
          <button
            type="button"
            class="bm-toc-icon"
            :title="`Show only ${row.node.name || '(no feature)'}`"
            :aria-label="`Show only ${row.node.name || '(no feature)'}`"
            data-testid="toc-zoom"
            @click="emit('zoom', row.node.anchor)"
          >
            ⤢
          </button>
        </span>
        <ul v-if="!row.collapsed && row.node.scenarios.length > 0" class="bm-toc-scenarios">
          <li
            v-for="scenario in row.node.scenarios"
            :key="scenario.uid"
            :data-status="scenario.status"
            :data-running="scenario.uid === running ? 'true' : undefined"
            data-testid="toc-scenario"
          >
            <button type="button" class="bm-toc-node" :title="scenario.uid" @click="emit('jump', `spec:${scenario.uid}`)">
              <span class="bm-toc-glyph">{{ glyph(scenario.status) }}</span>
              <span class="bm-toc-name">{{ scenario.title }}</span>
            </button>
          </li>
        </ul>
      </li>
    </ul>
    <p v-if="shown.features.length === 0 && filter" class="bm-toc-empty" data-testid="toc-empty">
      Nothing {{ label(filter) }}.
    </p>
  </nav>
</template>

<style scoped>
.bm-toc {
  font-size: 12px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.bm-toc-tools {
  display: flex;
  align-items: center;
  gap: 10px;
}

.bm-toc-follow {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--bm-menu-text);
}

.bm-toc-filter {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.bm-toc-chip {
  font: inherit;
  padding: 1px 8px;
  border: 1px solid var(--bc-neutral-800);
  border-radius: 999px;
  background: transparent;
  color: var(--bc-neutral-300);
  cursor: pointer;
}

.bm-toc-chip:hover {
  border-color: var(--bc-neutral-700);
}

.bm-toc-chip[data-active='true'] {
  border-color: currentColor;
}

.bm-toc-chip[data-status='running'] {
  color: var(--bm-state-running);
}

.bm-toc-chip[data-status='failed'] {
  color: var(--bm-state-failed);
}

.bm-toc-chip[data-status='passed'] {
  color: var(--bm-state-passed);
}

.bm-toc-chip[data-status='pending'] {
  color: var(--bm-state-unreached);
}

.bm-toc-suite {
  display: flex;
  align-items: center;
  gap: 6px;
  font-weight: 600;
  color: var(--bc-coat);
}

.bm-toc-features,
.bm-toc-scenarios {
  list-style: none;
  margin: 0;
  padding: 0;
}

.bm-toc-scenarios {
  flex-basis: 100%;
  padding-left: 14px;
}

.bm-toc-feature {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  padding-left: calc(var(--bm-depth, 0) * 12px);
}

.bm-toc-feature > .bm-toc-node {
  flex: 1;
  min-width: 0;
}

.bm-toc-twisty,
.bm-toc-zoom {
  flex: none;
}

.bm-toc-icon {
  padding: 0 3px;
  border: 0;
  background: transparent;
  color: var(--bm-menu-text);
  font: inherit;
  cursor: pointer;
}

.bm-toc-icon:hover {
  color: var(--bc-coat);
}

.bm-toc-zoom {
  visibility: hidden;
}

.bm-toc-feature:hover > .bm-toc-zoom,
.bm-toc-zoom:focus-within {
  visibility: visible;
}

.bm-toc-fold-all {
  margin-left: auto;
  display: inline-flex;
  gap: 2px;
}

.bm-toc-unzoom {
  margin-left: auto;
  padding: 0 6px;
  border: 1px solid var(--bc-neutral-800);
  border-radius: 999px;
  background: transparent;
  color: var(--bm-link);
  font: inherit;
  font-weight: 400;
  cursor: pointer;
}

.bm-toc-node {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 2px 4px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--bc-neutral-300);
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.bm-toc-node:hover {
  background: var(--bm-menu-hover);
  color: var(--bc-coat);
}

.bm-toc-feature-node {
  font-weight: 600;
  margin-top: 4px;
}

.bm-toc-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.bm-toc-glyph {
  flex: none;
  width: 1em;
  text-align: center;
}

[data-status='running'] > .bm-toc-node > .bm-toc-glyph,
.bm-toc-suite[data-status='running'] > .bm-toc-glyph {
  color: var(--bm-state-running);
}

[data-status='failed'] > .bm-toc-node > .bm-toc-glyph,
.bm-toc-suite[data-status='failed'] > .bm-toc-glyph {
  color: var(--bm-state-failed);
}

[data-status='passed'] > .bm-toc-node > .bm-toc-glyph,
.bm-toc-suite[data-status='passed'] > .bm-toc-glyph {
  color: var(--bm-state-passed);
}

[data-status='pending'] > .bm-toc-node > .bm-toc-glyph,
.bm-toc-suite[data-status='pending'] > .bm-toc-glyph {
  color: var(--bm-state-unreached);
}

li[data-running='true'] > .bm-toc-node {
  background: var(--bm-state-running-bg);
}

.bm-toc-rollup {
  display: inline-flex;
  gap: 6px;
  font-weight: 400;
}

.bm-toc-count[data-status='running'] {
  color: var(--bm-state-running);
}

.bm-toc-count[data-status='failed'] {
  color: var(--bm-state-failed);
}

.bm-toc-count[data-status='passed'] {
  color: var(--bm-state-passed);
}

.bm-toc-count[data-status='pending'] {
  color: var(--bm-state-unreached);
}

.bm-toc-empty {
  margin: 0;
  color: var(--bm-menu-text);
}
</style>
