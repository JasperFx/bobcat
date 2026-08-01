# Bobcat Monitor — AI Agent Coordination design notes

The monitor's second bounded context: a planning and progress surface for AI-agent-driven
development across the Critter Stack repositories. `docs/monitor-design.md` named this future
("an AI agent progress/planning visualization surface"); this epic is that future arriving.

The goal: see what work is at play, how it relates, what has already happened, and what is
next — as a dependency DAG spanning GitHub issues, pull requests, NuGet publishing, and
Bobcat-monitored test runs, across upstream and downstream repositories.

Decisions of record (2026-08-01):

## Prior art, and the gap this fills

Surveyed 2026-08-01: Beads (Yegge's agent-native graph issue tracker — validates dependency
DAG + agent-first JSON, but local/repo-scoped, no GitHub substrate, no publishing), Task
Master (PRD → task DAG + "next task" MCP tools — same validation, same limits), Vibe Kanban
and Conductor (worktree execution boards — "what are agents doing now", not "what blocks
what"), GitHub Agent HQ / Mission Control (GitHub-native agent session list — real and
shipping, but a flat list with **no dependency model at all**).

**Nobody models the cross-repo release train.** Issue B in Wolverine cannot start until
JasperFx merges, publishes 2.x.y, and Wolverine consumes it — and a test run is the evidence
gate at each hop. Publish/consume/upgrade as first-class DAG nodes with monitored test runs
as drill-in evidence is the differentiator, and it is precisely the Critter Stack's daily
coordination problem, so the dogfood is real work from day one.

GitHub native primitives were checked and do not cover the model: issue dependencies
(GA 2025-08) are **same-repo only**, sub-issues are org-scoped, and publish steps have no
GitHub identity of any kind. GitHub-native features are mirrors, never the model.

## Core stance: GitHub is the system of record, the monitor is a read model

Same posture as test runs. The monitor never owns issue or PR state — it folds observations
into projections. Two event provenances, kept distinct on every event:

- **Observed** — emitted by pollers (GitHub, NuGet feeds). Idempotent by construction:
  change-detection against the last-observed state, so a re-poll never appends a duplicate.
- **Asserted** — emitted by agents via MCP (claims, decisions, session metadata). Append-once
  by nature.

The dashboard can always answer *"does the plan believe this because GitHub said so or
because an agent said so."* Anything a poll can observe comes from the poll — a crashed
agent's issue must not render "in progress" forever (the orphaned-run rule, new hat).

Polling, not webhooks: this is a localhost dev tool. REST/GraphQL with etags, authenticated
(the repos are private). NuGet observation polls the nuget.org flat container index **and
authenticated private feeds and local folder feeds** — required in phase 2, not deferred,
because Bobcat's own packages may live on a private feed while the repo is private, and the
self-hosted dogfood plan (below) crosses exactly that hop.

## The plan model

A **plan document** (YAML, versioned, PR-able) is the source of truth for the DAG. Node kinds:

- `issue` — `org/repo#n`, cross-repo. Status derived: open → claimed (agent-asserted, plus an
  `agent:working` label mirror) → PR-open (closes-linkage) → merged → closed.
- `pr-gate` — merge policy is metadata: `merge-on-green` uses **GitHub's native auto-merge**
  (the monitor visualizes policy + status; no merge bot until native auto-merge demonstrably
  cannot express a need — the ITestHostLauncher rule), `needs-human-review` renders as a
  human-shaped node.
- `publish` — package + feed + bump kind (`Fix` / `Minor` / `Major`). The bump kind is
  consumed by whatever executes the publish (Nuke target, CI). **Done means the version was
  observed on the feed**, never that something claimed it published. Declared bump vs.
  actually-observed version mismatch renders as a fault, not silently reconciled.
- `consume` / `upgrade` — downstream repo takes the new version. Done when the version bump
  is observed in the downstream repo's committed package config.
- `test-run-gate` — a Bobcat-monitored suite as evidence. See drill-in below.

Edges are plain depends-on. Where GitHub can natively hold an edge (same-repo blocked-by),
the monitor may mirror it for people living in the GitHub UI — mirror only, phase 2+, and
the monitor stays read-only against GitHub in phase 1.

**Plans live in a dedicated planning repo, anchored by a GitHub issue each** — one issue per
plan (the epic's face: discussion, linkage, closure), body links to the plan file. The file
is the DAG; the issue is the anchor. GitHub Projects v2 is at most an optional human-facing
mirror, never a store — its custom-field API is clunky and two-way sync is a tar pit.

## Event-sourced on the Critter Stack, SQLite first

The coordination context is event-sourced from day one. The monitor's NDJSON + fold + replay
was already folk event sourcing; this is the same architecture with a real engine, and the
engine is arriving on purpose: **a SQLite-backed event store is being built in the Critter
Stack (~mid-August 2026) almost specifically for this tool.** The monitor is that store's
first consumer and requirements driver.

- **Storage gradient, chosen by connection string:** SQLite embedded default (zero config, a
  file beside today's archives) → Marten/Postgres (shared team box) → Polecat/SQL Server.
  Contracts are written against the **`JasperFx.Events` seam**, so the heavier stores are
  deployment options, not design decisions.
- **Streams:** per plan, per agent session, per publish node, per observed issue.
  Projections: `PlanProjection` (current DAG state), `AgentSessionProjection`, and the
  cross-cutting reads flat files made painful ("every issue this agent touched, any plan").
- **Wolverine subscriptions are the signaling mechanism.** `await_*` MCP tools park on
  subscriptions, not polling loops — a `PackageVersionObserved` event wakes every awaiter.
  The store is the coordination bus, not just memory. (Capacitor records; this orchestrates.)
- **Consumer requirements fed upstream early:** the embedded provider needs the async
  projection daemon (live projections, not inline-only) and Wolverine subscription support.
  Graceful degradation exists (inline projections, in-process waiters) but the store should
  know its first consumer wants both.
- **End state for storage:** the event store becomes the system of record for BOTH contexts;
  **NDJSON is demoted to an export format** (stays in the eject menu beside CTRF/JUnit,
  regenerable from streams). Hydration becomes projection rebuild; mtime-based retention
  becomes stream archival. The run-context migration is a known follow-on, not a
  precondition — but nothing new gets built on the hand-rolled fold.
- The never-slow-a-run invariant is untouched: it lives on the publisher side
  (fire-and-forget HTTP, bounded channel, drop on backpressure), not in the monitor's storage.

Two existing Bobcat threads land on this substrate:

1. **The issue #44 layer-2 failure ledger moves into this epic** as an early projection on
   the shared substrate. Its open questions (on-disk format, failure-class keying, concurrent
   CI append merging, aging) are what an event store answers by existing: append-only events,
   a projection keyed by `FailureSignature`, native concurrent appends, stream archival. The
   DECIDED fork survives intact as an event pair — `HintProposed` / `HintAccepted` — with the
   human-accepts audit trail built in. SQLite removes the old entry bar (CI appending to a
   shared Postgres).
2. **`KnownTestDurations`** stops being caller-supplied: a projection over run events feeds
   the supervisor's LPT balancing from real history.

## MCP tools for agents

Extends the existing `/api/mcp` surface (same stateless streamable-HTTP shape):
`get_plan`, `next_ready_nodes` (dependency-ordered ready work — the Task Master lesson),
`claim_issue`, `report_status`, `await_dependencies(nodeId)`, and
`await_package_version(feed, package, minVersion)`. The await pair is the token-optimization
mechanism — the `await_run_completion` pattern, already proven, generalized: agents block on
upstream instead of burning tokens polling GitHub and feeds themselves.

ai-skills content ships with the tools: the claim → work → PR → signal-downstream workflow,
written for agents that have never seen this conversation. (Mind the repo-visibility line —
ai-skills is public-facing; it documents the wire surface, not private internals.)

## Test-run drill-in

A `test-run-gate` node correlates to live runs via `BOBCAT_PLAN_NODE` (one more variable in
the `BOBCAT_RUN_ID` family, injected the same way). The plan node links to the `RunId`;
drilling in navigates to the **existing run card** — existing stores, existing views, zero
duplication. The DAG node renders running/passed/failed/flaky from the same projections, and
the node-state visual grammar **extends the existing color-token grammar**
(running blue / passed green / failed red / retrying orange, `--bm-` tokens) rather than
inventing a second one.

## Agent session memory (Layer B) — the Capacitor answer, scoped honestly

Kurrent's Capacitor (launched 2026-06, on KurrentDB) validated the thesis: event sourcing is
the right substrate for agent memory. The Critter Stack counter-story is the storage
gradient — **local-first SQLite file in a repo → team memory on Postgres/SQL Server** — plus
user-defined C# projections and *reactive* memory (subscriptions can wake downstream agents;
Capacitor records, it doesn't orchestrate). That product does not live in Bobcat: it is a
JasperFx thing, its own repo when extracted, and the clearest commercial candidate in this
whole epic. **This context is its proving ground** — the session-event schema, capture path,
and recall tools get proven here first.

**Capture depth: coordination metadata first.** Session started/finished, issue claimed,
decision summaries, hypothesis notes an agent explicitly commits, links to commits/PRs/runs.
Capture is a hook posting to `/api/ingest` — dependency-free, fire-and-forget, never slows
the agent (the `MonitorPublisher` contract). Full turn-by-turn transcript capture is a later
opt-in, once a projection actually wants it: transcripts are huge, and giant payloads
faulting wires is a fresh scar (the MTP ValueTooLarge lesson).

## Commercial boundary discipline

The repo is private (2026-08-01) while the commercial split is decided; the likely shape is
the CritterWatch precedent — libraries as the adoption funnel, consoles as the product. This
design must not foreclose any line:

- **Wire contracts are the seam.** The plan schema is a documented wire format; agents talk
  HTTP/MCP; no `Bobcat.*` library references the monitor (founding rule, unchanged).
- **Memory contracts ride `JasperFx.Events`** (OSS, JasperFx-owned), never a Bobcat
  assembly — the `DispositionKind` reasoning again. A future extraction moves deployables,
  never breaks contracts.
- Private-feed NuGet observation (above) is partly a consequence of this decision.

## Build order

1. **Plan schema + read-only DAG view** — plan document contract, GitHub poller (auth'd,
   etags, observation events), Vue DAG surface with drill-in navigation. Storage-agnostic
   work that fills the window until the SQLite store lands; the first stored projection is
   written directly against that store, pre-release included.
2. **NuGet observation** — nuget.org + private/local feeds; publish and consume nodes flip
   on observed evidence.
3. **MCP tools + ai-skills content** — the claim/report/await surface, subscription-backed.
4. **Test-run gate linkage** (`BOBCAT_PLAN_NODE`) — small once 1 exists.
5. **Session-memory capture** — metadata-depth, hook-based, into session streams.
6. **Failure-ledger projection** (#44 layer 2) on the shared substrate.

**The first real plan document is this epic itself**: SQLite event store in JasperFx →
publish → Bobcat.Monitor consumes → phases above, with the cross-repo publish/consume hop as
real nodes. Self-hosting the plan is the fastest way to find the schema's gaps and the best
possible demo.

## Parked, explicitly

- **Spinning up isolated agent sessions.** The mental model is noted for later — the monitor
  becomes to agents what `Supervisor` is to workers (lanes, purposes, environment
  injection) — but it is a separate epic, and the worktree-isolation execution layer is
  well-trodden ground elsewhere (Vibe Kanban, Conductor) that could be integrated rather
  than built.
- **Product extraction of Layer B.** Unscheduled until the shape is proven here.
- **Run-context storage migration** to the event store (known follow-on; NDJSON keeps
  working until then).
- **GitHub write-back mirrors** (labels, same-repo blocked-by edges) — phase 2+ at the
  earliest; phase 1 is strictly read-only against GitHub.
