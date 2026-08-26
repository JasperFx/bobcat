/**
 * TypeScript mirror of the `JasperFx.Events.EventModeling` wire descriptor (JasperFx.Events
 * 2.56.0, jasperfx#687 / #689 / #703 / #704).
 *
 * These are hand-written rather than generated, unlike the Bobcat console's monitor-event
 * mirrors: the descriptor is JasperFx's contract, not Bobcat's, so there is no local C# source
 * for a generator to read. `types.spec.ts` pins the enum members against the published shape so
 * a rename upstream fails here rather than rendering a blank lane.
 *
 * The elements/edges pair is deliberately the whole rendering contract — jasperfx#687 decision 9
 * calls it "the rendering contract every viewer can draw from without a second transform", which
 * is exactly why this package renders a descriptor and not any producer's private model.
 */

/** Kind of a rendered element. Maps 1:1 to a canonical Event Storming sticky-note colour. */
export type EventModelElementKind =
  | 'Trigger'
  | 'Command'
  | 'Handler'
  | 'Aggregate'
  | 'Event'
  | 'Message'
  | 'Projection'
  | 'ReadModel'
  | 'ExternalSystem'
  | 'Hotspot'

/** The four lanes of a standard Event Modeling canvas, top to bottom. */
export type EventModelLane = 'Wireframe' | 'Command' | 'EventStream' | 'ReadModel'

/** The four canonical Event Modeling slice patterns. */
export type SlicePattern = 'Command' | 'View' | 'Automation' | 'Translation'

/** What starts a slice. Finer-grained than textbook Event Modeling's human/external split. */
export type TriggerKind =
  | 'Http'
  | 'Grpc'
  | 'MessageHandler'
  | 'JobScheduler'
  | 'Human'
  | 'External'

/**
 * Where a hotspot came from.
 *
 * `SourceDisagreement` is jasperfx#704: two sources describing the same slice made different
 * claims about the same role and the merge had to drop one. It is emitted by the merge, never by
 * a source, and only when a claim is actually lost — two sources on one rung whose lists simply
 * union have not disagreed about anything.
 */
export type HotspotOrigin = 'PendingSpecification' | 'Prose' | 'SourceDisagreement'

/**
 * How much authority a source's claim carries — the three-rung ladder that decides precedence
 * when several sources describe the same slice (jasperfx#703). Higher rung wins, per claimed
 * role rather than wholesale.
 *
 * ⚠️ Not the same axis as CritterWatch's `LifecycleProvenance`, despite the overlapping member
 * name. That one is a RECONCILIATION of one edge across static and runtime discovery, where
 * `Confirmed` means "both sources agree" — not "seen in production". This is a ladder of
 * AUTHORITY. `Declared` and `Derived` both map onto its `Inferred`, `Observed` onto its
 * `Observed`, and its `Confirmed` has no rung here: agreement between rungs is expressed by the
 * ABSENCE of a `SourceDisagreement` hotspot rather than by a fourth value.
 */
export type EventModelProvenance = 'Declared' | 'Derived' | 'Observed'

/**
 * The slice members provenance is tracked against, so precedence is decided per claimed role.
 * Carried here only because a `SourceDisagreement` hotspot names one.
 */
export type EventModelRole =
  | 'TriggerLabel'
  | 'TriggerType'
  | 'TriggerKind'
  | 'TriggerOrigin'
  | 'Pattern'
  | 'CommandType'
  | 'HandlerType'
  | 'AggregateTypes'
  | 'EmittedEvents'
  | 'PublishedMessages'
  | 'ProjectionTypes'
  | 'ReadModelTypes'
  | 'ExternalSystems'
  | 'Hotspots'
  | 'Specifications'
  | 'Domain'

/** One source's claim about one role, as it stood before a merge resolved the disagreement. */
export interface EventModelClaim {
  provenance: EventModelProvenance
  /** Display rendering of what was claimed — short type names for the typed roles. */
  value: string
}

/** Which way messages flow between the model and an external system. */
export type ExternalSystemDirection = 'Inbound' | 'Outbound'

/** A CLR type identity. Only the members this package renders or keys on. */
export interface TypeDescriptor {
  name: string
  /** Optional on the wire — synthesize from `name` rather than assuming it. */
  fullName?: string
  assemblyName?: string
}

/** One rendered element of a slice. */
export interface EventModelElement {
  id: string
  kind: EventModelElementKind
  lane: EventModelLane
  label: string
  type?: TypeDescriptor | null
  /**
   * Which rung of the ladder claimed the role this element renders (jasperfx#703), stamped by the
   * producer from the owning slice so a viewer never re-derives it.
   *
   * Absent on a descriptor from JasperFx.Events < 2.56 and on the element kinds that have no role
   * of their own. Note that an *unattributed* model reads `Declared` rather than absent,
   * deliberately: that is the rung the merge actually treats it as.
   */
  provenance?: EventModelProvenance | null
}

/** A directed relationship between two elements of a slice, by element id. */
export interface EventModelEdge {
  // Edge endpoints are fromId/toId (element ids) — deliberately NOT source/target.
  fromId: string
  toId: string
}

/** A specification bound to a slice — Bobcat stamps `{Feature}/{Scenario}` into `identity`. */
export interface SpecificationDescriptor {
  identity: string
  feature?: string | null
  scenario?: string | null
  resolvedTypes?: TypeDescriptor[]
}

/** An open question, a conflict, or (primarily) a specification that is still pending. */
export interface HotspotDescriptor {
  origin: HotspotOrigin
  text: string
  specificationIdentity?: string | null
  /** The role two sources disagreed about. Set only when `origin` is `SourceDisagreement`. */
  role?: EventModelRole | null
  /** The claim the merge kept. Set only when `origin` is `SourceDisagreement`. */
  winningClaim?: EventModelClaim | null
  /**
   * The claim the merge dropped. Set only when `origin` is `SourceDisagreement`.
   *
   * A pair rather than a list because merges are pairwise: three sources disagreeing about one
   * role produce two hotspots, each naming the two claims that actually met.
   */
  losingClaim?: EventModelClaim | null
}

/** An external system on one end of an integration edge. */
export interface ExternalSystemDescriptor {
  name: string
  direction: ExternalSystemDirection
  endpointUri?: string | null
}

/** Wire descriptor for a single slice of an Event Model. */
export interface EventModelSliceDescriptor {
  name: string
  domain?: string | null
  pattern?: SlicePattern | null
  triggerKind?: TriggerKind | null
  triggerLabel?: string | null
  triggerOrigin?: string | null
  elements?: EventModelElement[]
  edges?: EventModelEdge[]
  specifications?: SpecificationDescriptor[]
  hotspots?: HotspotDescriptor[]
  externalSystems?: ExternalSystemDescriptor[]
}

/** Wire descriptor for an entire Event Model. */
export interface EventModelDescriptor {
  name: string
  slices?: EventModelSliceDescriptor[]
  aggregates?: EventModelElement[]
}

/** Lanes in canonical top-to-bottom order. Rendering order is part of the contract. */
export const LANE_ORDER: readonly EventModelLane[] = [
  'Wireframe',
  'Command',
  'EventStream',
  'ReadModel'
] as const

/** Ladder rungs, lowest authority first. Order is part of the contract. */
export const PROVENANCE_ORDER: readonly EventModelProvenance[] = [
  'Declared',
  'Derived',
  'Observed'
] as const

/** What each rung means, for a legend or a tooltip. */
export const PROVENANCE_LABEL: Record<EventModelProvenance, string> = {
  Declared: 'Declared — somebody wrote it down (a spec or the overlay)',
  Derived: 'Derived — read out of the code (Wolverine chains, the source generator)',
  Observed: 'Observed — seen happening in a running system'
}

/** Human-readable lane captions for the canvas gutter. */
export const LANE_LABEL: Record<EventModelLane, string> = {
  Wireframe: 'Wireframe / Trigger',
  Command: 'Command',
  EventStream: 'Event Stream',
  ReadModel: 'Read Model'
}
