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

`vue` and `@vue-flow/core` are peer dependencies — a consumer supplies its own, because bundling a
second copy of Vue gives you two reactivity systems that cannot see each other.

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
