# @jasperfx/event-model-vue

Vue renderer for a `JasperFx.Events.EventModeling.EventModelDescriptor`. Issue #108.

One descriptor, one picture, in two viewers: the Bobcat console renders design-time Event Models
here (MIT, free), and CritterWatch consumes this same package for its production surface. Because
MIT Bobcat cannot depend on BSL CritterWatch, the shared component lives in this repo.

## Why it renders a descriptor and not a lifecycle

CritterWatch's original `EventModelingView.vue` took its runtime `Lifecycle` model and transformed
it inside the component. That made it unshareable — the Bobcat console has no `Lifecycle`, and
CritterWatch has no Bobcat generator.

JasperFx.Events 2.54.0 (jasperfx#687) settled this upstream. `EventModelSliceDescriptor` carries
`Elements` and `Edges` as, in the contract's own words, "the rendering contract every viewer can
draw from without a second transform". So each producer adapts to the descriptor once —
`lifecycleToEventModel.ts` stays on the CritterWatch side, the Bobcat generator emits descriptors
directly (#106) — and this component stays common. The two surfaces now agree on a **published
type** rather than on a copied file, which is what actually guarantees identical rendering.

## Usage

```bash
npm install @jasperfx/event-model-vue
```

Published with provenance from `.github/workflows/publish-event-model-vue.yml` (npm Trusted
Publishing). Inside this repo the console consumes it as a `file:` link instead — same code, no
registry round-trip.

```ts
import { EventModelView } from '@jasperfx/event-model-vue'
import '@jasperfx/event-model-vue/style.css'
```

```vue
<EventModelView
  :descriptor="model"
  :slice-outcomes="outcomes"
  @element-click="drillDown"
/>
```

`slice-outcomes` maps a specification identity (`{Feature}/{Scenario}`) to `passed` / `failed` /
`notRun`, which is the run evidence issue #107 puts on the wire. A slice no evidence names stays
unmarked rather than defaulting to green.

`vue` is the one peer dependency — a consumer supplies its own, because bundling a
second copy of Vue gives you two reactivity systems that cannot see each other.

## Provenance, and disagreement between sources (0.4.0)

JasperFx.Events 2.56.0 (jasperfx#703 / #704) gave the descriptor a three-rung ladder —
`Declared < Derived < Observed` — and made a dropped claim a finding instead of a silent loss.
Four producers now feed one descriptor: Gherkin specs, the C# overlay, Wolverine's chains, and
CritterWatch's runtime observation. This package renders both halves.

**The ladder rides a second visual channel, never the fill.** Fill colour means the element KIND,
and two viewers agreeing on what a colour means is the whole reason this package exists. So
`EventModelElement.provenance` becomes a `data-provenance` attribute on the card plus a corner
marker on `Observed` only. `Declared` and `Derived` are deliberately left looking exactly as they
did: an unattributed model reads `Declared` rather than absent, so fading it would fade most of a
typical canvas — and the new information is "production has *seen* this", not "this was only
written down".

**A source disagreement is a finding, not another sticky.** `HotspotOrigin.SourceDisagreement` gets
`data-hotspot-origin` and a double outline, and its `winningClaim` / `losingClaim` go on the
tooltip. *The code says this slice emits `FundsWithdrawn`; production says `FundsWithdrawn` **and**
`AuditRecorded`* is arguably the most valuable thing a four-source model produces, and it deserves
better than the generic magenta every other hotspot gets.

⚠️ The producer projects each hotspot into an element with `ForLabel(name, Hotspot, hotspot.Text)`,
so the label **is** the hotspot text — that is the only join a viewer has back to the origin, and
`hotspotFor` relies on it.

Both fields are optional. A descriptor from a producer still on JasperFx.Events < 2.56 renders
exactly as it did on 0.3.0, with no attribute invented for a rung nobody claimed. Bobcat's own C#
still pins 2.54.0, so its generated descriptors are in that category until that pin moves.

⚠️ **Not the same axis as CritterWatch's `LifecycleProvenance`**, despite `Observed` appearing in
both. That one reconciles a single edge across static and runtime discovery, where `Confirmed`
means "both sources agree" — not "seen in production". This one is a ladder of authority.
`Declared` and `Derived` both map onto its `Inferred`, and its `Confirmed` has no rung here:
agreement is expressed by the *absence* of a `SourceDisagreement` hotspot.

## Edges are drawn, and routed here (0.6.0, bobcat#181)

`EventModelSliceDescriptor.Edges` is computed upstream from the typed roles on every read —
precisely so no renderer invents its own opinion about what connects to what — and this component
used to lay them out and then draw nothing. It draws them now, as one pointer-inert SVG layer
behind the cards.

**The route is computed in `layout.ts`, not in the component.** A polyline is as much a rendering
claim as a coordinate, so `LaidOutEdge.points` joins position in the pure layer: same descriptor,
same picture, checkable by a test. Two shapes, because the canvas has two kinds of relationship:

- **Along a lane** (command → handler → aggregate) the flow is left to right, so the edge is a
  straight line between the two facing card edges.
- **Across lanes** (command → event, event → projection) it is an orthogonal elbow turning at the
  middle of the lane gap. A diagonal would cross the band divider at an arbitrary angle and read as
  a different kind of statement; the elbow reads as "down into the next lane". A card sitting
  directly above its partner gets one straight drop instead of an elbow with two zero-length legs.

Both are direction-aware — an edge pointing back up or left leaves the *other* face of its source,
because declaration order is the producer's and nothing promises it matches flow order.

An edge whose endpoints were not both *drawn* is dropped, which is a slightly stronger test than
the "not both declared" one it replaces: an element in a lane this package does not know is
dropped from the canvas, and an arrow into empty space would read as a modelling claim rather than
as the producer bug it is.

## Reading a big model: zoom, pan, and what each slice is (0.7.0)

The 2026-08-31 review of a real 106-slice canvas produced four notes, all answered here so both
consoles inherit one answer.

**Zoom and pan (bobcat#182).** Zoom is a CSS transform on a wrapper, deliberately *not* a scale
factor threaded into `layoutEventModel`: layout is a pure function of the descriptor, and letting
the viewport change it would put the two viewers' agreement at the mercy of how wide someone's
window happens to be. The wrapper carries its own scaled `width`/`height`, because a transform does
not change layout size and the scroller would otherwise still think the canvas was its 100% self.
Stops (25%–200%) rather than a continuous ramp, so a reader can return to a zoom they had; the
level doubles as the reset. **Fit** is the one zoom that is not a step — "all of it on screen" is a
measurement, not a preference — and it never zooms *in* to fill, because a small model blown up to
200% looks like a mistake. It is clamped at 25% like every other zoom, so a genuinely enormous
model fits as far as legibility allows and no further. Drag-to-pan on the background; a drag that
starts on a button is not a pan.

**Bound-specification count (bobcat#183).** A badge on every slice header — `3 specs`, or `no spec`
spelled out, because zero is the drift case the canvas already colours orange and it should read as
a finding. Where the host passes `sliceOutcomes` the badge carries the verdict too, so a failing
slice says so at the same glance it says it has three specs.

**Trigger kind and routes (bobcat#184).** A 12px glyph per `triggerKind` in the slice header —
globe, envelope, clock, person, paired arrows, box-with-arrow — with `triggerOrigin` on its tooltip,
which is where the route lives now that wolverine#4181 stopped the HTTP source claiming
`TriggerLabel`. Inline path data rather than an icon dependency: this component ships to two
consoles with different icon sets, and a shared third is the one thing neither host wants. A trigger
card whose label *is* a route renders its verb as an outlined badge and the path beside it — the
verb is fixed vocabulary a reader recognises by shape, and outlined rather than filled because fill
means the element kind.

**A source disagreement reads as a finding (bobcat#178).** The producer makes the hotspot's text the
element label, so the card used to lead with the role name and a clipped sentence — and the reviewer
who designed the feature read it as a malformed events list and asked what it was for. That reading
was the finding. A hotspot card now renders structure: what kind of finding it is, then (for a
disagreement) the role and both claims with their rungs, kept above dropped and the dropped one
struck through. All from the typed `role`/`winningClaim`/`losingClaim`, so nothing is parsed back
out of the sentence, and a disagreement whose pair did not survive the wire degrades to its text
rather than to half a finding. Promoting findings out of the lanes into a strip above the canvas —
the other candidate on the issue — stays unbuilt: position is what says which slice a finding
belongs to, and a strip would have to repeat that in words.

## Card sizing: wrap, widen, then clamp (0.5.0, bobcat#180)

Cards were absolutely sized at 180px with `overflow: hidden`, so a long command name — and worse,
an HTTP trigger label like `POST /accounts/{id}/deposit` — was simply cut off mid-glyph. The
strategy is decided here once so both consoles inherit it, in this order:

1. **Wrap at the points a reader would break the name themselves.** CSS gives a browser break
   opportunities at spaces and hyphens and nowhere else, and `DepositMoneyIntoAccount` has neither
   — it is one unbreakable word. `segmentLabel` splits at camel humps and after `/ \ . _ - : , +`,
   and the card renders the segments with `<wbr>` between them.
2. **Widen the column only when wrapping is not enough.** `cardWidth` (180) became the *floor*, and
   a column grows to fit its own widest label in `LABEL_TARGET_LINES` (2) lines. Per column, not
   per card: cards in a column line up under each other, and ragged widths inside one lane read as
   a broken grid rather than as "this name is longer".
3. **Clamp past the cap.** `maxCardWidth` (320) stops one pathological route owning the canvas;
   beyond it the label clamps to `MAX_LABEL_LINES` (3) with an ellipsis. The full text was already
   on the card's tooltip, which is what makes truncation acceptable rather than lossy.

Set `maxCardWidth` equal to `cardWidth` to get the old fixed grid back.

**Widths are estimated, never measured** — `estimateTextWidth` is a small per-character model, not
`canvas.measureText` and not a DOM pass, because layout must stay a pure function of the descriptor
(below). The estimate only ever chooses a column width, and steps 1 and 3 absorb whatever it gets
wrong: an estimate 10% out costs a slightly roomy or slightly tight column, never a clipped name.
That is also why the type scale (`LABEL_FONT_SIZE`, `LABEL_LINE_HEIGHT`, `CARD_PADDING_X`) lives in
`layout.ts` and is written onto the card as inline style — a width computed from one font size and
rendered at another is a clipped label with no traceable symptom.

**Considered and rejected:** shrink-to-fit type scale (a canvas of six different text sizes reads
as noise, and the small end is unreadable at the zoom levels these are viewed at), and
truncate-with-title alone (it hides exactly the distinction — `OrderPlaced` vs `OrderPlacedV2` —
that the reader came to the canvas to see).

## Layout is pure, and that is the point

`layoutEventModel(descriptor, options)` is synchronous and free of any graph library — no elk, no
worker, no measurement pass. The acceptance criterion for this package is that the same descriptor
renders identically in both viewers, and "identically" is only checkable if position is a function
of the descriptor alone. `layout.spec.ts` therefore asserts exact coordinates.

Slices are vertical columns in declaration order; lanes are horizontal bands in the canonical
top-to-bottom order (`Wireframe`, `Command`, `EventStream`, `ReadModel`); an element sits where its
slice column meets its lane band, and several elements in one cell run left to right.

Three behaviours worth knowing, each pinned by a test:

- **Declaration order is preserved, never sorted.** It is the producer's statement about sequence.
- **A dangling edge is dropped.** Drawing a line to nowhere would read as a modelling claim rather
  than the producer bug it is.
- **An unknown lane is dropped, not stacked at y=0**, where it would overlap the wireframe lane and
  look like a rendering bug rather than "this descriptor came from a newer JasperFx".

## Development

```bash
npm install
npm test          # vitest run
npm run typecheck # vue-tsc -b
npm run build     # vite lib build + rolled-up .d.ts
```

CI gate: `.github/workflows/event-model-frontend.yml`.
