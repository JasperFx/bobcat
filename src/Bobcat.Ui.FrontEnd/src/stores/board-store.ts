import { defineStore } from 'pinia'
import { ref } from 'vue'
import { loadRetention, type RetentionPolicy } from '@/lib/retention'

/** The filter's name for runs from a path the server could not resolve to a repository. */
export const UNATTRIBUTED = '(unattributed)'

/**
 * What the board and the status strip share (bobcat#786): the repository filter — the strip's
 * live-run dropdown shows the runs the board is filtered to, so the two agree about which runs are
 * "here" — and the console's retention, loaded once for the board to state. Nothing here is
 * persisted; a filter is a thing a viewer sets while looking.
 */
export const useBoardStore = defineStore('board', () => {
  /** A repository slug, `UNATTRIBUTED`, or empty for every repository. */
  const repositoryFilter = ref('')

  /** Null until loaded; stays null when the console does not answer, and the board says nothing. */
  const retention = ref<RetentionPolicy | null>(null)
  let retentionAsked = false

  async function loadRetentionOnce(fetchImpl: typeof fetch = fetch): Promise<void> {
    if (retentionAsked) return
    retentionAsked = true
    retention.value = await loadRetention(fetchImpl)
  }

  /** A test seam: forget the filter and the retention, so one test never leaks into the next. */
  function reset() {
    repositoryFilter.value = ''
    retention.value = null
    retentionAsked = false
  }

  return { repositoryFilter, retention, loadRetentionOnce, reset }
})
