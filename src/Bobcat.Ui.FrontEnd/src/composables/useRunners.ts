import { onMounted } from 'vue'
import { storeToRefs } from 'pinia'
import type { Ref } from 'vue'
import type { RunnerView } from '@/lib/runners'
import { useRunnersStore } from '@/stores/runners-store'

/**
 * The attached runners, for every component that shows them — the runner list on the board
 * (bobcat#645), the commanded runs' card lines (bobcat#646), a run page's "before the last rebuild"
 * tag. Read from the runners store, which the console keeps current by pushing `runners_changed`
 * (bobcat#784); the first mount loads the list once if hydration has not yet. Nothing polls.
 */
export function useRunners(): { runners: Ref<RunnerView[] | null>; refresh: () => Promise<void> } {
  const store = useRunnersStore()
  const { runners } = storeToRefs(store)

  onMounted(() => {
    if (store.runners === null) void store.load()
  })

  return { runners, refresh: () => store.load() }
}

/** Forget what was loaded — a test seam, so one test's runners never leak into the next. */
export function resetRunners(): void {
  useRunnersStore().reset()
}
