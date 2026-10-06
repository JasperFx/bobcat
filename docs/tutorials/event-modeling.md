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

**One shape, as of issue #406: an [eventmodelers.ai](https://eventmodelers.ai) emlang board
export**, through `bobcat import-event-model`. It is segmented into slices and written out as
**C#** — one field-less stub record per command, event, aggregate and view, plus one
`EventModelDefinition` declaring the slices, patterns, triggers, chapters and domains through the
JasperFx.Events fluent API.

**YAML is no longer an authoring syntax.** The curated `.emodel.yaml` format was retired with
#406; the only YAML read anywhere is the Event Modeling platform's own, on import. The model you
edit afterwards is C# — which is the point: a stub type is something the compiler, the specs and
the derived model all already understand.

The segmentation is a set of reported guesses, and a wrong guess should be a one-line diff in the
generated code rather than a re-import. That framing is the point and should survive into the
tutorial. See [The `bobcat` Tool](../bobcat-tool.md#import-event-model).

### 3. Writing specs against the stubs

The specs are written against the stub types and are **red until the code exists, which is the
point**. Bobcat's own scaffolder was retired with #406 — skeleton generation for declared-only
slices is Wolverine's `scaffold` command (JasperFx/wolverine#4832), and the fallback is the AI
skill that turns red specs into code.

### 4. Implementing against the red spec

Red spec to working slice: the command handler, the aggregate, the events, the projection. As the
code grows the derived model takes over from the declared one, and any difference between them
shows up as a hotspot — which is the design-first to-do list.

### 5. Keeping the model and the code honest

- `[BobcatSlice]` binds a projected test to a slice — [Specs from tests you already have](../marker-steps.md#bobcatslice).
- [Checking Spec Identities Against the Model](../spec-identities.md) is the gate.

### 6. What still needs a hand

Two things need editing after generation, and **neither is a defect** — both are format
limitations, stated plainly so nobody files them as bugs:

- **`[property: Identity]` on command records.** The stream identity is a field of the command and
  nothing on an emlang board says which.
- **Stub records carry no fields at all.** A board's props are intentionally omitted, so a
  field-less stub is exactly what an import can honestly produce — the fields are yours to write,
  and that is not a gap in the importer.

## Where the material lives

| | |
|---|---|
| The `bobcat` tool and `import-event-model` | [The `bobcat` Tool](../bobcat-tool.md) |
| Slice binding and the ownership manifest | [Specs from tests you already have](../marker-steps.md) |
| The identity gate | [Checking Spec Identities Against the Model](../spec-identities.md) |
| **The curated format reference** | `ai-skills` — **by design**, not an oversight. No Bobcat doc documents the `then:` vocabulary or carries a grammar step table |
| A worked repository | CritterCrush, branch `crittercrush/regenerated-on-0.26.3` |

## Open question for the author

Whether this tutorial should restate any of the curated format, or link out to `ai-skills` for all
of it. Restating creates a second source of truth for a format still moving; linking out means the
tutorial cannot be followed in one sitting.
