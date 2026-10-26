import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import type {
  EventModelDescriptor,
  EventModelSliceDescriptor,
  HotspotDescriptor,
  SlicePattern
} from '@jasperfx/event-model-vue'
import type { ScenarioState } from '@/stores/runs-store'

/** One source's contribution to a model, as `GET /api/event-models` lists it (bobcat#799). */
export interface EventModelSourceSummary {
  source: string
  pushedAt: string
  /** True for a half stored before the console kept push times: `pushedAt` is then the file's write time. */
  pushedAtIsFileTime: boolean
  root: string | null
  repository: string | null
  branch: string | null
}

/** One model the console holds, its sources newest push first. */
export interface EventModelListingEntry {
  name: string
  lastPushedAt: string
  slices: number
  /** A source of it was pushed from a repository with runs on the board. */
  hasRunsOnBoard: boolean
  sources: EventModelSourceSummary[]
}

/**
 * `GET /api/event-models` (bobcat#799): every model the console holds. `current` is what the bare
 * `GET /api/event-model` serves (the model pushed last); `preferred` is the one the canvas opens on
 * when the URL names none: the newest model pushed from a repository with runs on the board, else the
 * newest of all.
 */
export interface EventModelListing {
  current: string | null
  preferred: string | null
  models: EventModelListingEntry[]
}

/** The read URL for one model, or for the current one when `name` is null. */
export function eventModelUrl(name: string | null): string {
  return name ? `/api/event-model?name=${encodeURIComponent(name)}` : '/api/event-model'
}

/**
 * The design-time Event Model (issue #108), read from GET /api/event-model — the descriptor a
 * producer pushed (Wolverine's `event-model` export, or what a spec assembly's generated
 * IEventModelDefinitionSource reported). Run evidence joins onto it by spec identity, which is
 * the scenario uid `{Feature}/{Scenario}` — one string on both sides of the design-time/run-time
 * seam, so no mapping table.
 *
 * bobcat#799: a console holds several models. `selected` names the one this store reads; null reads
 * the console's current one, which is what every page but the canvas wants and what this store did
 * before. The canvas selects one explicitly, so another application's push cannot swap it.
 */
export const useEventModelStore = defineStore('event-model', () => {
  const descriptor = ref<EventModelDescriptor | null>(null)
  /** 'idle' until the first load; 'absent' is a clean 404 — nothing published, not an error. */
  const status = ref<'idle' | 'loading' | 'loaded' | 'absent' | 'error'>('idle')
  /** The model this store reads, or null for the console's current one. */
  const selected = ref<string | null>(null)
  /** Every model the console holds; null until listed, or when listing failed. */
  const listing = ref<EventModelListing | null>(null)

  async function load(fetchImpl: typeof fetch = fetch) {
    status.value = 'loading'
    try {
      const response = await fetchImpl(eventModelUrl(selected.value))
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

  /** Read `name` (null for the current model) from here on, and load it. */
  async function select(name: string | null, fetchImpl: typeof fetch = fetch) {
    selected.value = name
    await load(fetchImpl)
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
      const response = await fetchImpl(eventModelUrl(selected.value))
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

  /** Read the console's models (bobcat#799). Leaves the last listing standing when the read fails. */
  async function loadModels(fetchImpl: typeof fetch = fetch): Promise<EventModelListing | null> {
    try {
      const response = await fetchImpl('/api/event-models')
      if (!response.ok) return listing.value
      listing.value = (await response.json()) as EventModelListing
    } catch {
      // The switcher and the header are decoration on the canvas; a failed listing leaves the diagram be.
    }
    return listing.value
  }

  /**
   * An `event_model_changed` push for `name` (#169, bobcat#799): the listing always moves (a push
   * time, perhaps a new model), but the diagram re-reads only when the push was of the model on
   * screen. A store reading the current model re-reads on any push, because any push can change
   * which model is current.
   */
  async function modelChanged(name: string | null | undefined, fetchImpl: typeof fetch = fetch) {
    const listed = listing.value !== null
    const redraw = selected.value === null || !name || name === selected.value
    await Promise.all([
      redraw ? refresh(fetchImpl) : Promise.resolve(),
      listed ? loadModels(fetchImpl) : Promise.resolve(null)
    ])
  }

  const slices = computed<EventModelSliceDescriptor[]>(() => descriptor.value?.slices ?? [])

  /** The listing's entry for the model on screen, when there is one. */
  const shownModel = computed<EventModelListingEntry | null>(() => {
    const name = descriptor.value?.name ?? selected.value
    return listing.value?.models.find((m) => m.name === name) ?? null
  })

  return {
    descriptor,
    status,
    selected,
    listing,
    load,
    select,
    refresh,
    loadModels,
    modelChanged,
    slices,
    shownModel
  }
})

/**
 * Where a source was pushed from, for the canvas header: the repository's `owner/name` when the push
 * said, else the checkout folder's name, with the branch when known. Null when the push said nothing.
 */
export function sourceOrigin(source: EventModelSourceSummary): string | null {
  const where = source.repository ?? (source.root ? (source.root.split(/[\\/]/).filter(Boolean).pop() ?? source.root) : null)
  if (!where) return null
  return source.branch ? `${where} (${source.branch})` : where
}

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
