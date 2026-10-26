<script setup lang="ts">
import type { Crumb } from '@/lib/crumbs'

/**
 * The trail back from a drill-down (bobcat#787): `Runs › Suite › run (time · tag · branch) › Feature
 * › Scenario`. Every segment but the last is a link; the last is where the reader is. The run
 * segment always carries the run's identity, so a trail never says "run" without saying which.
 */
defineProps<{ crumbs: Crumb[] }>()
</script>

<template>
  <nav class="bm-crumbs" aria-label="Breadcrumb" data-testid="breadcrumbs">
    <template v-for="(crumb, index) in crumbs" :key="index">
      <span v-if="index > 0" class="bm-crumb-sep" aria-hidden="true">›</span>
      <router-link
        v-if="crumb.to"
        :to="crumb.to"
        class="bm-crumb"
        :title="crumb.title"
        :data-testid="crumb.testId"
      >
        {{ crumb.label }}
      </router-link>
      <span v-else class="bm-crumb bm-crumb-current" :title="crumb.title" :data-testid="crumb.testId" aria-current="page">
        {{ crumb.label }}
      </span>
    </template>
  </nav>
</template>

<style scoped>
.bm-crumbs {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  margin-bottom: 10px;
  font-size: 12px;
  color: var(--bm-menu-text);
}

.bm-crumb {
  color: var(--bm-link);
  text-decoration: none;
  max-width: 40ch;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.bm-crumb:hover {
  text-decoration: underline;
}

.bm-crumb-current {
  color: var(--bc-coat);
}

.bm-crumb-sep {
  color: var(--bc-neutral-600);
}
</style>
