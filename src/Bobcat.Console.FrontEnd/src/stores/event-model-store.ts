import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import type { EventModelDescriptor, EventModelSliceDescriptor } from '@jasperfx/event-model-vue'
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

  const slices = computed<EventModelSliceDescriptor[]>(() => descriptor.value?.slices ?? [])

  return { descriptor, status, load, slices }
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
