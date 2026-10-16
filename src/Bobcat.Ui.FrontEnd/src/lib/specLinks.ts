import type { RouteLocationRaw } from 'vue-router'

/**
 * The two directions of stoat#61, spelled once. The join needs no mapping table: a spec's
 * identity is `{Feature}/{Scenario}` everywhere — the uid a scenario_finished carries, the one
 * `SpecNodeMapping.Uid` produces, and the one a design-time `SpecificationDescriptor` holds.
 */

/**
 * Specification → its rendered page within one run. The identity contains a slash, so it is
 * encoded into ONE path segment; the route's `(.*)` param takes it back whole either way.
 */
export function specHref(runId: string, uid: string): string {
  return `/runs/${runId}/specs/${encodeURIComponent(uid)}`
}

/**
 * Specification → the slice it is evidence for, on the Event Model: focused on that slice (the
 * renderer's own `focus=slice:` encoding, so the canvas lands there), with the drill-down open on
 * this spec and coloured from this run rather than whichever run happens to be newest.
 */
export function sliceHref(sliceName: string, uid: string, runId?: string | null): RouteLocationRaw {
  const query: Record<string, string> = { focus: `slice:${sliceName}`, spec: uid }
  if (runId) query.run = runId
  return { path: '/event-model', query }
}
