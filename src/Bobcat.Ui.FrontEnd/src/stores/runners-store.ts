import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { BuildStatusChanged, RunnersChanged } from '@/messages/monitor-events'
import { loadBuildStatus, type BuildStatusReported } from '@/lib/builds'
import { loadRunners, type RunnerView } from '@/lib/runners'

/**
 * The attached runners and the watched projects' builds (bobcat#784), as the console pushes them:
 * `runners_changed` and `build_status_changed` each carry the whole list, the same shape the GET
 * serves, so a push replaces what the page has. Loaded once over HTTP when the page hydrates, and
 * never polled — the board, a run page, a runner's page and the run buttons all read from here.
 *
 * `null` means "not loaded yet", which the panels render as nothing rather than as "no runner".
 */
export const useRunnersStore = defineStore('runners', () => {
  const runners = ref<RunnerView[] | null>(null)
  const builds = ref<BuildStatusReported[] | null>(null)

  /** The console pushed the runner list: a registration, a connection, a status, a command moving. */
  function handleRunnersChanged(e: RunnersChanged) {
    runners.value = (e.runners ?? []) as RunnerView[]
  }

  /** The console pushed every watched project's latest build. */
  function handleBuildStatusChanged(e: BuildStatusChanged) {
    builds.value = (e.builds ?? []) as BuildStatusReported[]
  }

  /**
   * The one HTTP load, on page load and on reconnect. Best-effort: a console that cannot answer
   * leaves what the page already has, and an unanswered first load reads as "no runner" rather than
   * as "loading" forever.
   */
  async function load(fetchImpl: typeof fetch = fetch): Promise<void> {
    const [foundRunners, foundBuilds] = await Promise.all([loadRunners(fetchImpl), loadBuildStatus(fetchImpl)])
    if (foundRunners !== null) runners.value = foundRunners
    else if (runners.value === null) runners.value = []
    if (foundBuilds !== null) builds.value = foundBuilds
    else if (builds.value === null) builds.value = []
  }

  /** The runner with this id in the latest push, or undefined. */
  function runnerById(runnerId: string): RunnerView | undefined {
    return runners.value?.find((r) => r.runnerId === runnerId)
  }

  /** Forget everything — a test seam, so one test's runners never leak into the next. */
  function reset() {
    runners.value = null
    builds.value = null
  }

  return { runners, builds, handleRunnersChanged, handleBuildStatusChanged, load, runnerById, reset }
})
