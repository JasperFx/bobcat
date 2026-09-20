# Event Modeling and Spec Driven Development

::: warning OUTLINE — NOT YET WRITTEN
This tutorial has no prose yet. Unlike the others, its material is not missing — it is **scattered
across nine pages and one external repository**, which is its own kind of gap: there is no single
path from "I have a model" to "I have a working slice."

The outline below records the path it needs to walk.
:::

## What this tutorial is for

Designing a system as an Event Model, then building it slice by slice — where the model is the
source of the specifications rather than a diagram that rots beside them.

## To cover

### 1. What an Event Model is here

Enough to orient someone who has not met [eventmodeling.org](https://eventmodeling.org), and no
more. The audience is a developer who wants the workflow, not the methodology.

### 2. Getting a model in

Two shapes, both handled by `bobcat import-event-model`:

- **The curated format** (`schema` / `model` / `slices`) — read and validated in place.
- **An [eventmodelers.ai](https://eventmodelers.ai) emlang board export** — segmented into slices
  and written out as a curated file to review.

The segmentation is a set of reported guesses, and a wrong guess should be a one-line diff in the
generated file rather than a re-import. That framing is the point and should survive into the
tutorial. See [The Command Line](../command-line.md#import-event-model).

### 3. Scaffolding specs from a slice

The generation path is proven — CritterCrush regenerates from a curated model with zero structural
edits and runs 88 specs green. This section is the walkthrough of doing that on a small model.

### 4. Implementing against the scaffolded spec

Red spec to working slice: the command handler, the aggregate, the events, the projection.

### 5. Keeping the model and the code honest

- `[BobcatSlice]` binds a projected test to a slice — [Specs from tests you already have](../marker-steps.md#binding-a-projected-test-to-a-slice-bobcatslice).
- The spec-ownership manifest says which slices are specified where, with `coveredBy` so the rule
  cannot rot, validated in both directions.
- [Checking Spec Identities Against the Model](../spec-identities.md) is the gate.

### 6. What still needs a hand

Two things need editing after generation, and **neither is a defect** — both are format
limitations, stated plainly so nobody files them as bugs:

- **`[property: Identity]` on command records.** The stream identity is a field of the command and
  nothing in the curated format says which. The scaffold's own comment points at the fix.
- **Status vocabularies are new files.** The format knows six scalar types, so every status arrives
  as `string`; enums are the preference but that granularity may be beyond the generator's scope.

## Where the material lives

| | |
|---|---|
| The `bobcat` tool and `import-event-model` | [The Command Line](../command-line.md) |
| Slice binding and the ownership manifest | [Specs from tests you already have](../marker-steps.md) |
| The identity gate | [Checking Spec Identities Against the Model](../spec-identities.md) |
| **The curated format reference** | `ai-skills` — **by design**, not an oversight. No Bobcat doc documents the `then:` vocabulary or carries a grammar step table |
| A worked repository | CritterCrush, branch `crittercrush/regenerated-on-0.26.3` |

## Open question for the author

Whether this tutorial should restate any of the curated format, or link out to `ai-skills` for all
of it. Restating creates a second source of truth for a format still moving; linking out means the
tutorial cannot be followed in one sitting.
