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
