<script setup lang="ts">
import { onMounted, ref } from 'vue'

/**
 * What a run or spec page says when the board no longer has its run (bobcat#646). The board keeps
 * the latest finished runs of each suite (#198), so a few presses of a run button evict the page a
 * person came from — which read as "Unknown run", a run that never was. An evicted run's archive is
 * kept, and the page says where.
 */
const props = defineProps<{ runId: string }>()

interface Whereabouts {
  state: 'board' | 'ejected' | 'unknown' | string
  archive: string | null
  retainedRuns: number
}

const where = ref<Whereabouts | null>(null)

onMounted(async () => {
  try {
    const response = await fetch(`/api/runs/${encodeURIComponent(props.runId)}/whereabouts`)
    where.value = response.ok ? ((await response.json()) as Whereabouts) : null
  } catch {
    where.value = null
  }
})
</script>

<template>
  <el-empty v-if="where?.state === 'ejected'" data-testid="run-evicted">
    <template #description>
      <p>This run is no longer on the board, but its archive is kept.</p>
      <p v-if="where.retainedRuns > 0" class="bm-missing-why">
        The board keeps the latest {{ where.retainedRuns }} finished runs of each suite, and this one was ejected
        (by that limit, or by hand). Raise it with <code>BOBCAT_MONITOR_RETENTION_RUNS</code>.
      </p>
      <p class="bm-missing-why">Archive: <code>{{ where.archive }}</code></p>
    </template>
  </el-empty>
  <el-empty v-else-if="where?.state === 'board'" description="Loading the run…" />
  <el-empty v-else description="Unknown run" data-testid="run-unknown" />
</template>

<style scoped>
.bm-missing-why {
  font-size: 12px;
}
</style>
