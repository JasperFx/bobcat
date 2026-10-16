import { onMounted, onUnmounted, ref, type Ref } from 'vue'
import { loadRunners, type RunnerView } from '@/lib/runners'

/**
 * The attached runners, polled once for the whole page however many components read them — the
 * runner list on the board (bobcat#645) and the commanded runs' card lines (bobcat#646) both do. The
 * registry is not pushed, so it is polled while anything on the page is watching.
 */
const runners: Ref<RunnerView[] | null> = ref(null)
let watchers = 0
let timer: ReturnType<typeof setInterval> | null = null

async function refresh(): Promise<void> {
  const found = await loadRunners()
  if (found !== null) runners.value = found
  else if (runners.value === null) runners.value = []
}

export function useRunners(pollMs = 5000): { runners: Ref<RunnerView[] | null>; refresh: () => Promise<void> } {
  onMounted(() => {
    watchers++
    if (watchers === 1) {
      void refresh()
      timer = setInterval(() => void refresh(), pollMs)
    }
  })

  onUnmounted(() => {
    watchers--
    if (watchers === 0 && timer !== null) {
      clearInterval(timer)
      timer = null
    }
  })

  return { runners, refresh }
}

/** Forget what was polled — a test seam, so one test's runners never leak into the next. */
export function resetRunners(): void {
  runners.value = null
  watchers = 0
  if (timer !== null) clearInterval(timer)
  timer = null
}
