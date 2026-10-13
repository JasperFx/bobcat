import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import type {
  EventModelDescriptor,
  EventModelSliceDescriptor,
  HotspotDescriptor,
  SlicePattern
} from '@jasperfx/event-model-vue'
import type { ScenarioState } from '@/stores/runs-store'

/**
 * The design-time Event Model (issue #108), read from GET /api/event-model — the descriptor a
 * producer pushed (Wolverine's `event-model` export, or what a spec assembly's generated
 * IEventModelDefinitionSource reported). Run evidence joins onto it by spec identity, which is
 * the scenario uid `{Feature}/{Scenario}` — one string on both sides of the design-time/run-time
 * seam, so no mapping table.
 */
export const useEventModelStore = defineStore('event-model', () => {
  const descriptor = ref<EventModelDescriptor | null>(null)
  /** 'idle' until the first load; 'absent' is a clean 404 — nothing published, not an error. */
  const status = ref<'idle' | 'loading' | 'loaded' | 'absent' | 'error'>('idle')

  async function load(fetchImpl: typeof fetch = fetch) {
    status.value = 'loading'
    try {
      const response = await fetchImpl('/api/event-model')
      if (response.status === 404) {
        descriptor.value = null
        status.value = 'absent'
        return
      }
      if (!response.ok) throw new Error(`GET /api/event-model answered ${response.status}`)
      descriptor.value = (await response.json()) as EventModelDescriptor
      status.value = 'loaded'
    } catch {
      status.value = 'error'
    }
  }

  /**
   * Issue #169 — re-read after an `event_model_changed` push, WITHOUT the load path's visible
   * transitions.
   *
   * `load` moves status to 'loading' and lets a failure become 'error', which is right on first
   * mount and wrong for a live refresh: the page already has a good diagram on screen, and a
   * producer saving a file should never make it flash a spinner or replace it with an error. So
   * this assigns only on success and leaves the previous descriptor standing otherwise — the next
   * push will correct it.
   *
   * A 404 IS assigned, because "the model was deleted" is a real answer rather than a failure.
   */
  async function refresh(fetchImpl: typeof fetch = fetch) {
    try {
      const response = await fetchImpl('/api/event-model')
      if (response.status === 404) {
        descriptor.value = null
        status.value = 'absent'
        return
      }
      if (!response.ok) return
      descriptor.value = (await response.json()) as EventModelDescriptor
      status.value = 'loaded'
    } catch {
      // Keep whatever is on screen. A dropped refresh is not worth blanking a good diagram.
    }
  }

  const slices = computed<EventModelSliceDescriptor[]>(() => descriptor.value?.slices ?? [])

  return { descriptor, status, load, refresh, slices }
})

/**
 * Fold a run's scenarios onto the descriptor's spec identities, in the shape
 * EventModelView's `sliceOutcomes` prop wants. Every identity the descriptor declares gets an
 * entry: a scenario with a verdict maps to passed/failed, and an identity the run never
 * reached (or that is still running) is 'notRun' — which is what colours drift, so it must be
 * stated rather than omitted. Identities the descriptor does not declare contribute nothing.
 */
export function outcomesFor(
  descriptor: EventModelDescriptor | null,
  scenarios: Record<string, ScenarioState>
): Record<string, 'passed' | 'failed' | 'notRun'> {
  const outcomes: Record<string, 'passed' | 'failed' | 'notRun'> = {}
  for (const slice of descriptor?.slices ?? []) {
    for (const spec of slice.specifications ?? []) {
      const outcome = scenarios[spec.identity]?.outcome
      outcomes[spec.identity] =
        outcome === 'CleanPass' || outcome === 'PassOnRetry'
          ? 'passed'
          : outcome === 'Failed' || outcome === 'Aborted'
            ? 'failed'
            : 'notRun'
    }
  }
  return outcomes
}

/**
 * The run evidence a slice's bound scenarios did not declare (issue #107 meets #106): touched
 * types whose fullName is neither a declared element type of the slice nor among the spec's
 * own resolvedTypes. Non-empty means the spec exercised types the model does not show — the
 * yellow of drift colouring, surfaced in the drill-down.
 */
export function undeclaredTouches(
  slice: EventModelSliceDescriptor,
  scenario: ScenarioState | undefined
): string[] {
  if (!scenario) return []
  const declared = new Set<string>()
  for (const element of slice.elements ?? []) {
    if (element.type?.fullName) declared.add(element.type.fullName)
  }
  for (const spec of slice.specifications ?? []) {
    for (const resolved of spec.resolvedTypes ?? []) {
      if (resolved.fullName) declared.add(resolved.fullName)
    }
  }
  return scenario.touchedTypes.filter((t) => !declared.has(t.fullName)).map((t) => t.fullName)
}

/**
 * The slice a specification is evidence for (stoat#61) — the one whose `specifications` name this
 * identity. Null when no slice binds it, which is an answer ("design-time only"), not an error.
 * First binding wins if a model binds one spec to two slices; the link lands somewhere true.
 */
export function sliceFor(
  descriptor: EventModelDescriptor | null,
  uid: string
): EventModelSliceDescriptor | null {
  for (const slice of descriptor?.slices ?? []) {
    if ((slice.specifications ?? []).some((spec) => spec.identity === uid)) return slice
  }
  return null
}

/**
 * What each slice pattern means, for the drill-down's pattern tag. A `Record` over the package's own
 * `SlicePattern` union, so the next pattern the renderer learns fails the type check here until it
 * has a label, rather than rendering blank. `Query` (jasperfx#1003) is the Critter Stack's addition:
 * Wolverine derives every read-only GET as one.
 */
export const PATTERN_LABEL: Record<SlicePattern, string> = {
  Command: 'Command — a state change',
  View: 'View — events into a read model',
  Automation: 'Automation — a reaction, no human in the loop',
  Translation: 'Translation — an integration edge',
  Query: 'Query — a read, no state change'
}

/**
 * The label for a slice's pattern. Never throws and never renders blank: a pattern no source has
 * derived says so, and one newer than this build (a producer ahead of the renderer) shows as given.
 */
export function patternLabel(pattern: SlicePattern | string | null | undefined): string {
  if (!pattern) return 'pattern not derived'
  return (PATTERN_LABEL as Record<string, string>)[pattern] ?? pattern
}

/**
 * The roles a slice carries that its pattern rules out (jasperfx#1003) — a Query that appends events,
 * starts a stream or sends a command. Computed upstream and carried apart from `hotspots`; absent
 * from a producer below the JasperFx.Events release that added it, which reads as none.
 */
export function patternViolationsOf(slice: EventModelSliceDescriptor | null | undefined): HotspotDescriptor[] {
  return slice?.patternViolations ?? []
}
