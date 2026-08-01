import { defineStore } from 'pinia'
import { computed, ref } from 'vue'

/**
 * The coordination context's read side. Unlike runs — which stream over SignalR — plan
 * status changes at the observers' polling cadence (GitHub / NuGet sweeps), so the store
 * refetches the derived-status endpoint rather than folding live events. The plan DAG view
 * polls while mounted; a server-side push contract can replace that when the event-sourced
 * status layer lands.
 */

export interface PlanSummary {
  slug: string
  title: string
  source: 'file' | 'pushed'
  sourcePath: string | null
  valid: boolean
  nodes: number
  errors: string[]
  loadedAt: string
}

export interface NodeStatus {
  id: string
  kind: string
  title: string
  status: string
  ready: boolean
  ref: string | null
  detail: string | null
  observedTitle: string | null
  assignees: string[] | null
  openPrs: number[] | null
  observedAt: string | null
  dependsOn: string[]
  /** Set on test-run-gate nodes — the drill-in target on the runs side. */
  runId: string | null
  /** Agent holding a live monitor claim (leased assertion — expires if the agent dies). */
  claimedBy: string | null
  /** The claim's latest report_node note — the agent's own word. */
  note: string | null
}

export interface PlanStatus {
  slug: string
  title: string
  nodes: NodeStatus[]
  ready: string[]
}

export const usePlansStore = defineStore('plans', () => {
  const summaries = ref<PlanSummary[]>([])
  const statuses = ref<Record<string, PlanStatus>>({})
  /** Slugs whose status endpoint answered 409 — registered, but the document has errors. */
  const invalid = ref<Record<string, string[]>>({})

  const allPlans = computed(() => summaries.value)

  function summaryOf(slug: string): PlanSummary | null {
    return summaries.value.find((p) => p.slug === slug) ?? null
  }

  function statusOf(slug: string): PlanStatus | null {
    return statuses.value[slug] ?? null
  }

  async function fetchPlans(fetchImpl: typeof fetch = fetch): Promise<void> {
    try {
      const response = await fetchImpl('/api/plans')
      if (!response.ok) return
      summaries.value = (await response.json()) as PlanSummary[]
    } catch {
      // Best-effort: a dead backend keeps the last list rather than blanking the view.
    }
  }

  async function fetchStatus(slug: string, fetchImpl: typeof fetch = fetch): Promise<void> {
    try {
      const response = await fetchImpl(`/api/plans/${encodeURIComponent(slug)}/status`)

      if (response.status === 409) {
        const body = (await response.json()) as { errors?: string[] }
        invalid.value = { ...invalid.value, [slug]: body.errors ?? ['invalid plan document'] }
        return
      }

      if (!response.ok) return

      const status = (await response.json()) as PlanStatus
      statuses.value = { ...statuses.value, [slug]: status }
      if (slug in invalid.value) {
        const next = { ...invalid.value }
        delete next[slug]
        invalid.value = next
      }
    } catch {
      // Same stance as fetchPlans: stale status beats a blank DAG.
    }
  }

  return { summaries, statuses, invalid, allPlans, summaryOf, statusOf, fetchPlans, fetchStatus }
})
