# Event Model canvas: cause→effect links and zooming into parts of the model

Design of record, drafted 2026-09-12. Research first; nothing here is built. Companion to the
Event Model page section of `docs/monitor-design.md` (issue #108 onward), which this does not
replace.

## The ask

1. **Arrows between related cause-and-effect actors.** An event emitted in one slice should
   visibly feed the projection, automation, or handler that consumes it in another slice — the
   arrows an Event Modeling board draws across its timeline.
2. **Zoom in and out on parts of the model.** Not "make everything smaller" (we have that) but
   "look at this region, then step back out".

## What exists today — and why the ask is not what it first looks like

Two facts change the shape of the work.

**The canvas already draws edges.** Since 0.6.0 (#181) `layout.ts` routes every edge a slice
carries — straight along a lane, an orthogonal elbow through the lane gap across lanes
(`src/Bobcat.EventModel.FrontEnd/src/layout.ts:194-224`) — and `EventModelView.vue:659-688`
draws them as one pointer-inert SVG polyline layer with a shared arrowhead marker. What it
draws is exactly what the descriptor says, and **the descriptor only ever says something about
one slice at a time**:

- `EventModelSliceDescriptor.Elements`/`.Edges` are computed on read by `buildGraph()`
  (`jasperfx/src/JasperFx/Descriptors/EventModeling/EventModelSliceDescriptor.cs:442-570`). The
  rules are Trigger→Command→Handler→{Aggregate, Event, Message}, Event→Projection→ReadModel (or
  Event→ReadModel with no projection), Message→ExternalSystem, ReadModel→Trigger for a pure view
  slice. Element ids are `{slice}/{kind}/{type}`, so the *same* event type in two slices is two
  ids, and `layout.ts:264-266` routes edges against a per-slice map on purpose.
- `EventModelDescriptor` carries `Name`, `Slices`, `Aggregates` (a flat, de-duplicated list of
  aggregate types with their `AppliedEvents` — not a graph) and `Hotspots`. No model-level edge
  list, no slice-to-slice link, no ordering beyond declaration order, no chapter, no position.
- **There is no role for what a slice reads.** `ReadModelTypes` is documented as "read-model
  types the slice reads from *or* produces" and Wolverine folds `[ReadModel]`/`[Entity]`
  parameters (reads) and `IStorageAction<T>` returns (writes) into the same list
  (`wolverine/src/Wolverine/Configuration/EventModeling/EventModelRoles.cs:154-158, 333-341`).
  There is no `ConsumedEvents` on a slice; the one place "consumes these events" is recorded is
  `AggregateDescriptor.AppliedEvents` at model level.
- The only cross-slice join in the stack is Wolverine's `FinishModel`
  (`WolverineEventModelSource.cs:387-403`): a slice whose `CommandType.FullName` is some other
  slice's emitted event is re-patterned `Automation`. It produces a pattern, not an edge.

So "arrows between cause and effect" is first a **descriptor question** — what relationship is
derivable, from which roles, computed where — and only second a rendering question.

**The canvas already zooms and pans.** 0.7.0 (#182) added a CSS `transform: scale()` on a
wrapper (never a scale threaded into the pure layout), nine discrete stops from 0.25 to 2,
Ctrl/⌘-wheel, drag-to-pan, fit-to-width (`EventModelView.vue:146-260`). 0.8.0 fixed the zoom
anchor. #194 then found the ceiling: CritterWatch's merged model is 106 slices and ~10,000px
wide at the 25% floor, and concluded "zoom is the wrong lever past a certain size … render fewer
slices, not smaller ones" — the filter bar. What is missing is not magnification but
**navigation**: focus on a region, see less detail when far out, know where you are, and follow
a relationship to wherever it lands.

## What other Event Modeling tools do

Surveyed 2026-09-12. Every tool in the space renders with SVG (none use `<canvas>`/WebGL at
these sizes), and every one draws only the arrows the four patterns define.

| Tool | Substrate | Cross-slice arrows | Navigation | Notes |
|---|---|---|---|---|
| **eventmodelers.ai / Nebulit** (Miro app + own web canvas) | Miro-native app; own SVG canvas for the web toolkit | Data-flow arrows drawn by the modeller; the emlang YAML export carries *no* arrows — relationships are name references and step order within a chapter | **Chapters**: "a wide blue arrow spanning several slices groups them into a named chapter — useful once a model grows past a single screen's width and needs a table of contents"; "zoom into a single timeline … then return to the full board"; a **zoom-in model** — a second model for one complex slice, the main model stays clean | Swimlanes = bounded contexts. AI validation and codegen. Bobcat's `bobcat import-event-model` reads this export (#202) — and **drops the chapter at the descriptor boundary**; only `module` → `Domain` survives (`src/Bobcat.EventModel/Emlang/EmlangImport.cs:41-51, 122, 161`) |
| **emlang toolchain** (Go, MIT) | `emlang diagram <file>` → static HTML, CSS-variable themed | Implicit from step order and name references | None documented | Text-as-source-of-truth sibling of Bobcat's curated YAML (#201) |
| **oNote → Evident Design** | Browser SVG canvas | Data-flow arrows "to identify Command and Read Model APIs and Event Streams" | Pan/zoom, real-time collaboration | The Dymitruk lineage |
| **sparkle-space/event-modeler** (archived 2026-04) | Elixir/Phoenix + custom SVG, JS hook for pan/zoom/drag | Arrows with **connection-rule validation** (which kinds may connect) | Pan/zoom, slices as named groupings | Generated Given/When/Then from the relationships. Authors moved on to "open source alternatives … including Mermaid support" |
| **Modellution** | Web platform, EventStorming + Event Modeling | Yes | Collaboration, estimates, codegen | Commercial |
| **Miro / Figma templates** | Sticky notes and hand-drawn connectors | Whatever the team draws; long arrows across the timeline are the well-known pain | Frames as chapters; native minimap and zoom | Still where most real models live |
| **EventCatalog** (adjacent: EDA docs, not a timeline) | `@xyflow/react`, rebuilt layout engine, domain groups with headers | Service→event→service graph, not slices | **Minimap**, hover highlighting done by direct DOM manipulation (not framework state), memoized nodes, animations paused while panning/zooming | The most polished navigation in the neighbourhood, and its lessons transfer even though its layout is a graph rather than a grid |

Three things stand out.

1. **Nobody auto-derives cross-slice arrows from code.** Every tool draws what a human drew, or
   infers from step order in one chapter. Bobcat's descriptor has typed roles from four
   provenance rungs; a derived, always-current cause→effect graph is something none of them has.
2. **Chapters are the universal answer to "zoom into a part".** eventmodelers.ai, Miro frames,
   emlang's per-chapter documents. The descriptor has only `Domain` as a grouping axis, and the
   emlang import throws the chapter away.
3. **Long arrows are treated as a cost to be managed**, not a feature: eventmodelers.ai's
   "zoom-in model" and the Event Modeling convention of repeating a sticky where it is consumed
   both exist to avoid a 3,000px connector.

## Technology options for the canvas

The shared package (`@jasperfx/event-model-vue`, consumed by the Bobcat console and CritterWatch)
has **no runtime dependencies** — `vue` is its only peer. That is a decision of record:
`@vue-flow/core` was in the peer list and was dropped because npm 7+ installs peers into every
consumer (`docs/monitor-design.md:400-402`, commit 5679e2d). The layout is a pure, synchronous,
library-free function whose acceptance criterion is coordinate-identical rendering in both
consoles (`layout.ts:11-24`).

| Option | What it buys | What it costs | Verdict |
|---|---|---|---|
| **A. Extend the hand-rolled substrate** — pure layout + absolutely-positioned cards + one SVG layer | A link router, level-of-detail classes, focus-fit and a minimap are each a few hundred lines over data we already compute; stays deterministic and pinned by `layout.spec.ts`; both consoles move together on a version bump | We write a channel router and a minimap ourselves | **Recommended.** Event Modeling is a *fixed grid* — lanes are rows, slices are columns in declaration order. Every hard thing a graph library solves (where do nodes go?) is already solved by convention; what remains is routing and navigation over known coordinates |
| **B. Vue Flow** (`@vue-flow/core`) | Viewport pan/zoom (d3-zoom), `fitView({ nodes })`, minimap, custom nodes, smoothstep edges, hover state | A node *editor* — draggable and stateful by default; ~100 KB+ with the d3 subset; edge types are endpoint-only (no obstacle or channel awareness, so a long edge still cuts through cards); replaces a pure tested layout with a store; re-opens the peer-dependency wound; CritterWatch would have to adopt it too | Rejected. We would use it for the viewport and fight it for everything else |
| **C. ELK** (`elkjs`, `elk.layered` + partitioning for lanes) | Real layered layout and orthogonal routing with obstacle avoidance | 1.5 MB+ JS/WASM, async, output moves across versions, and it wants to *choose* column order — which the descriptor deliberately fixes ("declaration order is the producer's statement about sequence", `layout.ts:261-263`) | Rejected. The timeline is the layer assignment; ELK would be asked to rediscover it and then be corrected |
| **D. `d3-zoom` + `d3-selection` only** | Cursor-anchored continuous zoom with the conventional gesture semantics | ~30 KB and a runtime dependency for what is a ~60-line pointer/wheel handler over the wrapper we already scale | Rejected; hand-roll the gesture |
| **E. tldraw / Excalidraw / Konva** | Editing | Everything — this is a read-only viewer of a document the sources compute | Not applicable |

The one place a library earns consideration is **routing many long edges without crossings**,
and the answer there is structural rather than algorithmic: do not draw many long edges.

## Design

### 1. Cross-slice links are a descriptor fact, computed upstream on read

The standing rule for slice edges is "only roles are stamped, never a graph; the graph is
computed on read so there is one opinion about it" (`CLAUDE.md`, Event Modeling slice tags).
The same rule applies one level up. A cross-slice link is a claim about the model — an agent
asking the MCP `event-model` tools "what consumes `OrderPlaced`?" or Stoat reading
`GET /api/event-model` must get the same answer the canvas draws, and neither loads a Vue
package.

**Proposal (JasperFx):** `EventModelDescriptor.Links`, a computed, read-only property in the
image of `EventModelSliceDescriptor.Edges` — never serialized as input, recomputed on every
read, present on the wire as `links` because the console's `EventModelStore` round-trips a push
through the typed descriptor (`src/Bobcat.Console/EventModel/EventModelStore.cs:142-176`). A
document pushed by a host on an older JasperFx still gets links, from the console's version.

```csharp
public sealed record EventModelLink(
    string FromSlice, string FromElementId,   // "{slice}/{kind}/{type}" — the ids Elements already carry
    string ToSlice,   string ToElementId,
    EventModelLinkKind Kind,
    TypeDescriptor Via);                       // the type identity the join matched on

public enum EventModelLinkKind
{
    EventTriggers,      // A emits E; B's CommandType/TriggerType is E  (Automation / Translation)
    MessageTriggers,    // A publishes M; B handles M                   (cascading messages)
    EventConsumed,      // A emits E; B's projection/read model applies E (State View) — phase 3
    ReadModelRead,      // A produces R; B reads R before deciding        (Automation input) — phase 3
}
```

Type identity follows the rule `mergeTypes` already encodes
(`EventModelSliceDescriptor.cs:343-361`): key on `FullName` when both sides carry a non-empty
`AssemblyName`, else on short `Name`, because an empty assembly name marks a declaration whose
`FullName` is a synthesized guess (`CuratedModelMapper.cs:29`).

**Two of the four kinds are derivable today**, from roles every rung already stamps:

- `EventTriggers` / `MessageTriggers`: `B.CommandType ∪ B.TriggerType` ∈ `A.EmittedEvents ∪
  A.PublishedMessages`. This is exactly the join `FinishModel` performs to flip a pattern; the
  pattern derivation should move down to sit on the same computation so there is one rule, not
  a Wolverine copy. A slice never links to itself.
- A **shared aggregate** (two slices with the same `AggregateTypes` entry) is deliberately *not*
  a link. It is a stream, and Event Modeling draws a stream as a row, not an arrow — see the
  open question on stream sub-rows below.

The other two need roles the vocabulary does not have — phase 3.

### 2. Rendering: local reference cards, corridor-routed arrows, highlight on selection

The Event Modeling convention for a consumed event is to **repeat the sticky where it is
consumed**, not to draw a connector back to where it was produced. The descriptor already
repeats: a consumed event appears as an element in the consuming slice (its `CommandType`, its
`Event` in a view slice). So the local half of the picture is nearly free.

- **Reference cards.** An element that is the `To` end of a link renders with an origin chevron
  (`◂ WithdrawFunds`) in its header. The intra-slice arrows it already has (Command→Handler,
  Event→Projection) tell the local story; the chevron says where the input came from. Clicking
  the chevron **jumps** — pans and, if needed, fits — to the origin element and flashes it. This
  is what makes cause→effect navigable at 106 slices, where a 3,000px arrow is unreadable
  whatever the router does.
- **Corridor-routed arrows.** Links are still drawn, because at a readable size seeing the arc
  from event to automation is the point. They are routed in the **lane gaps**, which the fixed
  grid gives us for free: leave the source card downward into the gap below its lane, run
  horizontally along a *track* within that gap, and enter the target card from above/below.
  Tracks are allocated per gap left-to-right so parallel runs do not overprint (a first-fit
  interval assignment — the sequence-diagram/swimlane router, ~100 lines, pure, in `layout.ts`,
  pinned by `layout.spec.ts` like `routeEdge`). Because a link always runs in a gap, it never
  crosses a card. A link that starts and ends in the same lane runs in the gap below that lane.
- **Bundling by source.** All links leaving one element share one trunk from the card to the
  track and fan out along it. The trunk *is* the event stream leaving the fact; four consumers
  are four branches off one line, not four lines. This is the single biggest clutter reduction
  and it matches the mental model.
- **Highlight on selection, faint at rest.** At rest, links draw at low opacity like edges do
  now (`opacity: 0.45`, `EventModelView.vue:1047-1060`) but thinner. Hover or select a card, or
  select a slice, and its links and their far ends light up; everything else dims. EventCatalog
  does this with direct DOM class toggling rather than reactive state, and so should we — a
  hover over one of 400 cards must not re-render the graph. A three-state control in the
  toolbar — **links: all / selected / none** — for the reader who wants a clean board.
- **One glyph per kind.** `EventTriggers` and `MessageTriggers` solid with the existing
  arrowhead; `EventConsumed` dotted; `ReadModelRead` dashed. No labels on links (the `Via`
  type is the far end's label already); the kind is available in the drill-down drawer.
- **Off-screen ends.** A card whose link partner is outside the viewport shows a small count
  badge (`⇢ 3`) in place of the truncated arrow; clicking it jumps. Computed from the scroll
  position, not the layout.

What does not change: `layoutEventModel` stays pure and synchronous; links become a fourth
output (`links: LaidOutLink[]` with `points` and `track`) beside `edges`; the SVG layer gains a
second `<g>`; the TypeScript mirror in `types.ts` gains `EventModelLink`/`EventModelLinkKind`
pinned by `types.spec.ts` as the enums are today.

### 3. Zooming into parts: focus, level of detail, minimap, place

Four features, in the order they pay for themselves. All ride the existing CSS-transform
wrapper; none touch the layout.

1. **Focus.** Select a slice (or a domain band, or a card) and *Focus* fits its **neighbourhood**
   — the slice plus every slice one link away — into the viewport, and dims the rest. A
   breadcrumb (`Banking › Accounts › WithdrawFunds`) steps back out; Esc clears. Fit-to-rect is
   trivial over the wrapper (`scale = min(vw/w, vh/h)` clamped to the zoom range, then scroll),
   and the neighbourhood comes from the same `links` the arrows use — focus is the first
   consumer that makes cross-slice links worth having beyond the picture. This is
   eventmodelers.ai's "zoom into a single timeline" and "zoom-in model" without a second model.
2. **Level of detail.** Zoom is geometric today: at 0.25 a card is a 45px smear of 8px text.
   Semantic zoom swaps *what* a card shows by scale, via a `data-lod` attribute on the viewport
   that CSS switches on — no re-layout, identical in both consoles by construction:
   - `detail` (≥ 0.7): what renders today.
   - `compact` (0.4–0.7): kind colour, short label, provenance/spec badge; glyphs, verb badges
     and hotspot text drop.
   - `overview` (< 0.4): cards become colour blocks with no text; each slice paints its **name
     and pattern** once across its column; lane captions stay; links draw as bundled trunks only.
     A domain band header is legible when nothing else is, which is what a 106-slice overview
     is for.
3. **Minimap.** A second rendering of the same `EventModelGraph` as bare `<rect>`s at ~1/40
   scale in the corner, with the viewport as a draggable window. Rects only — no text, no
   edges — so it costs nothing to draw and tracks the filter bar's hidden slices for free
   because it consumes the same laid-out graph. Every tool in the survey with a large board
   has one; this is the standard answer to "where am I on 10,000px".
4. **Place.** Continuous, cursor-anchored wheel zoom (the nine stops stay as the button ladder);
   and the console page (not the package) mirrors zoom, focus and selection into the route
   query so a URL to "CreditWallet, focused" can be pasted into a PR. The package emits
   `viewport-change`; the page decides whether to persist it.

Domain is the only grouping axis the descriptor has, so the focus hierarchy is model → domain →
slice → bound spec (the existing drawer). Chapters would make it model → chapter → slice — see
open questions.

### 4. Phasing

Filed 2026-09-12: jasperfx#823 (phase 0), bobcat#295 (1), bobcat#296 (2), jasperfx#824 +
jasperfx#825 + wolverine#4419 + bobcat#297 (3), bobcat#298 (4), bobcat#299 (5); all linked
from the #257 tracking issue.

| Phase | Where | Delivers | Needs |
|---|---|---|---|
| **0** | JasperFx (jasperfx#823) | `EventModelDescriptor.Links` computed on read: `EventTriggers`, `MessageTriggers`; `FinishModel`'s automation rule re-based on it; `links` on the wire | JasperFx bump; `types.ts` mirror + `types.spec.ts` pin; `EventModelStoreTests` round-trip asserting `links` regenerate from a roles-only push |
| **1** | `@jasperfx/event-model-vue` | Reference chevrons, corridor router with track allocation and source bundling, selection highlight, links toggle, off-screen badges, jump | Fixtures gain a two-slice event→automation model; `layout.spec.ts` pins the tracks; both consoles get it by version bump |
| **2** | `@jasperfx/event-model-vue` + console page | Focus with neighbourhood and breadcrumb, `data-lod` levels, minimap, continuous zoom, `viewport-change` → route query | No upstream change |
| **3** | JasperFx + every source | `ConsumedEvents` and `ReadsFrom` roles → `EventConsumed` / `ReadModelRead` links, the State View and Automation-input arrows | Wolverine splits `[ReadModel]`/`[Entity]` reads from `IStorageAction<T>` writes; a JasperFx.Events source reads the store's projection registry (there is **no** Marten/Polecat/Fisher-derived source today — `ProjectionTypes` is populated only by Bobcat's declared sources and CritterWatch's generator); curated YAML and the emlang import declare them (a `v:` step's preceding `e:` steps in its chapter are its inputs); Bobcat's generator stamps an arranged `{event}` in a View slice as consumed (decision 3) |
| **4** | Bobcat import + JasperFx | `Chapter` on a slice, preserved from the emlang board; chapter bands as the focus hierarchy (decision 5) | Upstream property; a curated-file field; `EmlangImport` stops discarding the name |
| **5** | `@jasperfx/event-model-vue` | Per-aggregate stream sub-rows inside the `EventStream` lane (decision 2) | Own issue; changes every event's `y` and the lane height, so `layout.spec.ts` and both consoles' fixtures move with it |

Phases 0–2 stand alone and deliver the ask. Phase 3 is where the *State View* arrow — event to
read model across slices, the arrow people most expect — becomes derivable, and it is honestly
the expensive one because the vocabulary has never recorded what a slice reads.

## Decision (2026-09-16): `@pattern:` — a spec states its pattern rather than guessing it

Issue #323. Every Automation slice carried a `SourceDisagreement` it had no way to resolve:

```
Pattern: Declared claims Automation; Declared claims Command
```

The curated model says `Automation`. The spec source inferred `Command`, because a slice that
receives a command is a Command slice — and `When X is received` is the same sentence for a bus
command and for an automation's trigger event. Both claims sit on the **Declared** rung, so the
merge picks one and leaves a hotspot that no change to the model *or* the code can clear. Three of
CritterCrush's ten hotspots were this, and they were the only three nobody could act on.

**Rejected: abstain on bus acts.** Returning null whenever the act is `is received` would have
removed the wrong guess and the right ones with it — this repo's own `Deliveries.feature` is the
saga lane, and its acts are real commands received off the bus. Inference is correct there and
should stay.

**Decided: a tag, because a tag can say what a sentence cannot.** `@pattern:Automation` sits beside
`@slice:`, `@domain:` and `@chapter:`, is parsed by both tag parsers (pinned by
`SliceTagParsingAgreementTests`), and the scaffolder writes it from the curated model's `pattern:`.
Inference is untouched and still runs when no tag is present, so a hand-written feature behaves
exactly as before.

The wider point, which generalises past Pattern: **when a `.feature` is generated from a curated
model, the two are one design intent registered twice.** Anywhere the spec can only infer what the
model states outright, that arrangement manufactures disagreements. Carrying the model's answer
through in a tag is the general fix, and Pattern is the first case of it.

Not fixed here, because it is not ours: the hotspot text names both sides `Declared` and neither
source, so the message reads as one source contradicting itself. Two sources sharing a rung is the
normal case for spec-first work, and a claim needs a source identity alongside its rung —
JasperFx.Events owns that. Filed upstream.

## Decisions (2026-09-12)

The five questions this draft left open were decided the same day.

1. **Links are derived upstream, in JasperFx.** `EventModelDescriptor.Links` computed on read,
   like slice `Edges`; no client-side derivation and no fallback. The alternative — derive in
   `layout.ts` — would have shipped without a JasperFx release, but it leaves the wire document
   without the relationship, which is the thing every non-Vue consumer needs.
2. **A shared aggregate becomes a stream sub-row, under its own issue.** Canonical Event
   Modeling draws one row per stream inside the event lane, so two slices on `Account` put
   their events on the same horizontal line and the relationship is visible with no arrow. The
   descriptor has `AggregateTypes` per slice and `Aggregates` per model, so the layout can split
   `EventStream` into per-aggregate sub-rows. It changes every event's `y` and the lane height —
   a layout change, not a link kind, sequenced after phases 0–2 (phase 5 below).
3. **An arranged `{event}` in a View slice stamps `ConsumedEvents`.** The #259 Given demotion
   stands for Command slices — arranged history there is the aggregate's stream, not something
   the slice consumes — but `Given AccountOpened occurred … Then the AccountBalance read model
   contains` is the best evidence there is that `AccountBalance` consumes `AccountOpened`, and
   stamping it *consumed* is a different claim from stamping it emitted. A spec-only project
   gets State View arrows with no code-derived source. Lands in phase 3, once the role exists
   upstream; touches the generator's role emission and `EventModelDescriptorTests`.
4. **At `detail` zoom, both: chevrons always, arrows faint.** Every consumed element carries its
   `◂ origin` chevron; corridor-routed arrows draw at low opacity and light up on hover or
   selection; the toolbar toggle (all / selected / none) is for readers who want a clean board.
   Revisit only if CritterWatch's 106-slice model shows the router is not enough.
5. **Chapter becomes a first-class slice grouping.** An optional `Chapter` on
   `EventModelSliceDescriptor`, a curated-file field, and `EmlangImport` stops discarding the
   board's chapter name. The focus hierarchy becomes model → chapter → slice, matching
   eventmodelers.ai and Miro frames. Phase 4 is confirmed, not conditional.

## Sources

- eventmodelers.ai: [tutorial](https://eventmodelers.ai/docs/event-modeling-tutorial/),
  [cheat sheet — 5 elements, 4 patterns, 20 rules](https://www.eventmodelers.ai/cheatsheet/),
  [Miro app](https://miro.com/marketplace/eventmodeling/), [web toolkit](https://app.eventmodelers.ai/)
- [emlang](https://emlang-project.github.io/) and the [toolchain](https://github.com/emlang-project/emlang)
- [sparkle-space/event-modeler](https://github.com/sparkle-space/event-modeler) (archived 2026-04)
- [oNote](https://www.onote.com/blog/event-modeling-explained/) / Evident Design, via
  [eventmodeling.org resources](https://eventmodeling.org/resources/)
- [Modellution](https://www.modellution.com/)
- EventCatalog: [visualizer improvements](https://www.eventcatalog.dev/blog/visualizer-improvements),
  [visualization](https://www.eventcatalog.dev/features/visualization)
- Layout engines: [React Flow layouting overview](https://reactflow.dev/learn/layouting/layouting),
  [Vue Flow](https://vueflow.dev/guide/), [ELK pipeline cookbook](https://stately.ai/docs/packages/graph/react-flow-elk-pipeline)
- Semantic zoom: [Jjodel](https://arxiv.org/pdf/2502.09146), [semantic zoom + minimaps](https://arxiv.org/pdf/2510.00003)
