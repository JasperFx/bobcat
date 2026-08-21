/**
 * TypeScript mirror of the `JasperFx.Events.EventModeling` wire descriptor (JasperFx.Events
 * 2.54.0, jasperfx#687 / #689).
 *
 * These are hand-written rather than generated, unlike the Bobcat console's monitor-event
 * mirrors: the descriptor is JasperFx's contract, not Bobcat's, so there is no local C# source
 * for a generator to read. `descriptor.contract.spec.ts` pins the enum members against the
 * published shape so a rename upstream fails here rather than rendering a blank lane.
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

/** Where a hotspot came from. */
export type HotspotOrigin = 'PendingSpecification' | 'Prose'

/** Which way messages flow between the model and an external system. */
export type ExternalSystemDirection = 'Inbound' | 'Outbound'

/** A CLR type identity. Only the members this package renders or keys on. */
export interface TypeDescriptor {
  name: string
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
}

/** A directed relationship between two elements of a slice, by element id. */
export interface EventModelEdge {
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

/** Human-readable lane captions for the canvas gutter. */
export const LANE_LABEL: Record<EventModelLane, string> = {
  Wireframe: 'Wireframe / Trigger',
  Command: 'Command',
  EventStream: 'Event Stream',
  ReadModel: 'Read Model'
}
