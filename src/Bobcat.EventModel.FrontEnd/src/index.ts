export { default as EventModelView } from './EventModelView.vue'
export {
  layoutEventModel,
  COLLAPSED_WIDTH,
  CARD_PADDING_X,
  LABEL_FONT_SIZE,
  LABEL_LINE_HEIGHT,
  LABEL_TARGET_LINES,
  MAX_LABEL_LINES
} from './layout'
export { estimateTextWidth, requiredContentWidth, segmentLabel } from './text'
export type {
  EventModelGraph,
  LaidOutLane,
  LaidOutNode,
  LaidOutSlice,
  LayoutOptions
} from './layout'
export { EVENT_MODEL_PALETTE, colorFor, inkFor, DASHED_KINDS, OUTLINED_KINDS } from './palette'
export { LANE_ORDER, LANE_LABEL, PROVENANCE_ORDER, PROVENANCE_LABEL } from './types'
export type {
  EventModelClaim,
  EventModelDescriptor,
  EventModelEdge,
  EventModelElement,
  EventModelElementKind,
  EventModelLane,
  EventModelProvenance,
  EventModelRole,
  EventModelSliceDescriptor,
  ExternalSystemDescriptor,
  ExternalSystemDirection,
  HotspotDescriptor,
  HotspotOrigin,
  SlicePattern,
  SpecificationDescriptor,
  TriggerKind,
  TypeDescriptor
} from './types'
