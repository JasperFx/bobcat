<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRunners } from '@/composables/useRunners'
import { buildProblems, loadBuildStatus, type BuildStatusReported } from '@/lib/builds'

/**
 * Says a project does not compile, with its first compiler errors in view (bobcat#651), wherever the
 * page shows that project's runs: the board, or one suite's run page when `suite` is given.
 */
const props = withDefaults(defineProps<{ suite?: string | null; pollMs?: number }>(), { suite: null, pollMs: 5000 })

const builds = ref<BuildStatusReported[] | null>(null)
const { runners } = useRunners()
let timer: ReturnType<typeof setInterval> | null = null

async function refresh(): Promise<void> {
  const found = await loadBuildStatus()
  if (found !== null) builds.value = found
}

onMounted(() => {
  void refresh()
  timer = setInterval(() => void refresh(), props.pollMs)
})

onUnmounted(() => {
  if (timer !== null) clearInterval(timer)
})

const problems = computed(() =>
  buildProblems(builds.value, runners.value).filter((p) => props.suite === null || p.suite === props.suite),
)
</script>

<template>
  <div v-if="problems.length > 0" class="bm-builds" data-testid="build-status">
    <el-alert
      v-for="problem in problems"
      :key="problem.suite"
      :type="problem.failed ? 'error' : 'warning'"
      :closable="false"
      class="bm-build"
      data-testid="build-problem"
    >
      <template #title>
        <span v-if="problem.failed">{{ problem.suite }} does not compile</span>
        <span v-else>{{ problem.suite }} is rebuilding</span>
        <span class="bm-build-source"> · {{ problem.source }}</span>
      </template>
      <div v-if="problem.failed" class="bm-build-note">
        Its runs below are of the code before the change that broke the build.
      </div>
      <pre v-if="problem.failed && problem.detail" class="bm-build-errors" data-testid="build-errors">{{ problem.detail }}</pre>
    </el-alert>
  </div>
</template>

<style scoped>
.bm-builds {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-bottom: 16px;
}

.bm-build-source,
.bm-build-note {
  font-size: 12px;
  opacity: 0.8;
}

.bm-build-errors {
  margin: 6px 0 0;
  white-space: pre-wrap;
  font-size: 12px;
}
</style>
