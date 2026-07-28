# Spike findings: driving an MTP test host from a parent orchestrator

**Issue:** [#43](https://github.com/JasperFx/bobcat/issues/43) — validates decision 2 of [#41](https://github.com/JasperFx/bobcat/issues/41).
**Date:** 2026-07-28
**Verdict: GO.** Adopt Microsoft.Testing.Platform as the #41 worker protocol.

Everything below was measured by the spike harness in this directory, not read from docs.
Reproduce with:

```bash
cd spikes/mtp-orchestration
dotnet build MtpSpike.sln
cd Spike.Orchestrator
dotnet run -- \
  ../Spike.XunitV3Host/bin/Debug/net10.0/Spike.XunitV3Host \
  ../Spike.TUnitHost/bin/Debug/net10.0/Spike.TUnitHost \
  ../Spike.BobcatHost/bin/Debug/net10.0/Spike.BobcatHost
```

Last run: **36 findings — 30 pass, 2 partial, 4 fail.** All six non-passes are two issues
(exception-type erasure, crash salvage), each recorded once per host. Both are worked around
below; neither is load-bearing.

---

## Verdict summary

| # | Question | Answer |
|---|----------|--------|
| Q1 | Parent can launch a host and collect per-test results? | **Yes** — all three hosts |
| Q2 | Works across xUnit v3, tUnit, and a Bobcat-owned host? | **Yes** — identical wire shape |
| Q3 | Selective re-run by id + run-alone + stable identity? | **Yes** — all three levers work |
| Q4 | Traits/attributes readable through the protocol? | **Yes** — at discovery time, no reflection |
| Q5 | Failure fidelity good enough to classify? | **Mostly** — one real gap (exception type) |
| Q6 | Process model viable? | **Yes** — ~90–110ms startup, clean cancellation |

---

## The transport (not what the docs led me to expect)

Server mode is **not** stdio JSON-RPC, and `--server` is **not listed in `--help`**. It is a
hidden option, and the transport is **TCP with the roles inverted**: the parent listens, the
test host dials back.

```
parent: TcpListener on 127.0.0.1:0                      -> port N
parent: launch host  --server jsonrpc --client-port N
host:   connects to  127.0.0.1:N
both:   JSON-RPC 2.0, LSP framing (Content-Length header, blank line, UTF-8 body)
```

Also worth keeping: `--exit-on-process-exit <pid>` makes a host die with its supervisor, which
removes a whole class of stranded-worker bugs for free.

Handshake, verbatim from `initialize`:

```json
{"serverInfo":{"name":"test-anywhere","version":"1.9.1"},
 "capabilities":{"testing":{"supportsDiscovery":true,
   "experimental_multiRequestSupport":false,"vstestProvider":false,
   "attachmentsSupport":true,"multipleConnectionProvider":false}}}
```

Note `experimental_multiRequestSupport: false` — see *Cost and risks* below.

### There is no client library

Microsoft ships no JSON-RPC client for this; IDEs implement it themselves. The supervisor owns
that code. In this spike it is **~230 lines** (`JsonRpcConnection.cs`) plus ~200 lines of
session/result mapping. That is the true entry price, and it is modest.

---

## Q1 — Enumerate, run, collect structured results

Works against all three hosts. Test nodes arrive as `testing/testUpdates/tests` notifications;
`"changes": null` marks the end of a run. A node is a **flat object of dotted keys** — *not* the
`$type`-tagged property bag the in-process object model uses. Real captured node:

```json
{
  "uid": "17342a96cd715121c649fa0072ea5a050a6a84a9604b84ec15c697af82a013c3",
  "display-name": "Spike.XunitV3Host.SpikeTests.flaky_until_told_otherwise",
  "location.file": ".../SpikeTests.cs", "location.line-start": 38,
  "location.type": "Spike.XunitV3Host.SpikeTests", "location.method": "flaky_until_told_otherwise",
  "time.duration-ms": 0.7411,
  "node-type": "action",
  "execution-state": "failed",
  "error.message": "flaky test failing (set SPIKE_FLAKY_PASSES=true to pass)",
  "error.stacktrace": "   at Spike.XunitV3Host.SpikeTests...",
  "assert.actual": "", "assert.expected": ""
}
```

Observed `execution-state` values: `discovered`, `in-progress`, `passed`, `failed`, `error`,
`skipped`. Every host reported source file + line, and duration for every test.

---

## Q2 — Cross-host

| Host | Discovered | States | Notes |
|------|-----------|--------|-------|
| xUnit v3 3.2.2 | 9 | passed/failed/error/skipped | uid is a SHA-256-looking hash |
| TUnit 1.62.0 | 8 | passed/failed/error/skipped | uid is structural: `Ns.Class.1.1.method.1.1.0` |
| Bobcat-owned | 8 | passed/failed/error/skipped | uid is whatever we choose |

**Both halves of the #41 seam are proven.** The Bobcat-owned host
(`Spike.BobcatHost`) is a plain console app whose entire MTP surface is:

```csharp
var builder = await TestApplication.CreateBuilderAsync(args);
builder.RegisterTestFramework(
    _ => new TestFrameworkCapabilities(),
    (caps, sp) => new BobcatSpecFramework(caps, sp));
using var app = await builder.BuildAsync();
return await app.RunAsync();
```

plus an `ITestFramework, IDataProducer` implementation of **~90 lines**. Bobcat's Gherkin runner
becoming an MTP host is genuinely small work — and it lights up `dotnet test`, IDE Test
Explorers, and CI for free.

**uids are opaque and must be treated as such.** They range from a hash to a dotted structural
path to an arbitrary string. Never parse, split, or sort by them.

---

## Q3 — Selective re-run, isolation, identity stability

All three levers #41 needs are available. **This is the result that decides the issue.**

- **Identity is stable across processes.** Two separate host processes produced byte-identical
  uid sets for all three frameworks. Retries can target the right test.
- **Re-run one test by id, in the same process** (`RetryInProcess`): asked for 1 uid, host
  executed exactly 1 test. Works on all three.
- **Run one test alone in a fresh process** (`RetryInFreshProcess` / `[Isolated]`): a new host
  process told up front to run a single uid executed exactly that test, and honoured per-process
  environment variables — so the isolated attempt really does own its environment.

### Trap: the subset parameter is `tests`, and getting it wrong fails silently

The `testing/runTests` subset parameter is **`tests`** — an array of bare test nodes:

```json
{"runId":"…","tests":[{"uid":"…","display-name":"…","node-type":"action"}]}
```

I first sent `testNodes` (the name that appears in the platform assembly's internal
`RunRequestArgs.TestNodes`), and then `testCases`. **Neither errored.** The platform silently
ignored the unknown property and ran the *entire suite*, which the parent cannot distinguish
from a filter that matched everything. I only caught it because the Bobcat host logged the
filter type it received (`NopFilter` instead of `TestNodeUidListFilter`).

Brute-forced shape matrix, against the instrumented Bobcat host:

| Payload | Result |
|---|---|
| `tests: [node]` | ✅ `TestNodeUidListFilter`, 1 test |
| `testNodes: [node]` | ⚠️ `NopFilter`, **8 tests, no error** |
| `testCases: [node]` | ⚠️ `NopFilter`, **8 tests, no error** |
| `testNodes: ["uid"]` | ⚠️ `NopFilter`, **8 tests, no error** |
| `tests: [{node:{…}}]` | ❌ error −32602 `'uid' field is missing` |
| `filter: {…}` | ❌ error −32602 type mismatch |

**Implication for #41:** a retry that silently re-runs the whole suite would be a catastrophic
correctness bug — it would launder unrelated failures into a retry attempt. The supervisor must
**assert that the returned outcome count matches the requested count** and treat a mismatch as a
protocol fault, not trust the request. This is cheap insurance and should be written into the
worker client from day one.

There is also a CLI-level `--filter-uid` (this one *is* documented in `--help`), verified
working: `./Spike.XunitV3Host --filter-uid <uid>` → `total: 1`. That is a viable fallback for
the fresh-process cases and a useful cross-check.

---

## Q4 — Metadata passthrough

**Yes, and better than hoped: traits arrive at *discovery* time, before anything runs.** No
separate reflection pass is needed, which means the policy engine can plan isolation and
recycling *before* scheduling.

xUnit's `[Trait("Isolated","true")]` and tUnit's `[Property("Isolated","true")]` land on the
wire in the **same shape**, an array of single-key objects:

```json
"traits": [{"RecycleOnRetry":"rabbit"}, {"Isolated":"true"}]
```

The Bobcat host emits the same thing via `TestMetadataProperty`. So #41's plan — key the policy
engine off `[Isolated]` / `[RecycleOnRetry("rabbit")]` surfaced through MTP metadata — works
as written, across all three front-ends, with one parser.

---

## Q5 — Failure fidelity

### Good: assertion vs exception is a native distinction

The wire separates `execution-state: "failed"` (an assertion disagreed) from
`execution-state: "error"` (an exception escaped), and all three frameworks map onto it the same
way, independently. Assertion failures additionally carry `assert.actual` / `assert.expected`.

That is exactly the `FailAndContinue`-vs-something-worse split `Disposition` needs, available for
free. Stack traces came through for every failure on every host.

### Gap: the exception TYPE is not on the wire

There is **no `error.type` field**. What each host does with it:

- **xUnit v3** formats `error.message` as `Namespace.ExceptionType : message`, so the type is
  recoverable *by string convention only*.
- **tUnit** emits `[Test Failure] rabbit went away` — the type is **gone**.
- A **Bobcat-owned host** can put whatever it likes in the message, so this is only a problem
  for the foreign front-ends.

**This matters** because #41 says the policy engine "keys off exception-type mapping" for
xUnit/tUnit tests. That plan does not survive contact with the wire.

**Recommended response — lean on Q4 instead of Q5.** Traits are reliable, structured, present at
discovery, and identical across frameworks; exception types are none of those things. Make
attribute/trait-driven policy (`[Isolated]`, `[RecycleOnRetry("rabbit")]`) the **primary**
mechanism, and treat exception-type matching as a best-effort secondary that reads
`error.message` + `error.stacktrace` with an explicitly per-framework matcher. This is the same
pluggable-matcher shape the test-projector uses, so it is not new machinery — but the *default*
should be traits, not types. It also means the docs must tell Critter Stack users to tag flaky
integration tests rather than rely on us sniffing `NpgsqlException`.

### Crash detection: yes. Crash salvage: not reliable.

Killing the host mid-run (`Environment.Exit(70)` inside a test) was **cleanly detectable on all
three hosts**: the socket closes, the in-flight `testing/runTests` request faults rather than
hanging, and the exit code (70) is readable. Good.

But **how much completed work reaches the parent before death is not guaranteed** — MTP batches
node updates and flushes on idle:

| Host | Outcomes salvaged before death |
|---|---|
| xUnit v3 | **0** of 9 |
| TUnit | 7 of 8 |
| Bobcat-owned | **0** of 8 |

**Implication for #41:** the supervisor must treat every test in an in-flight slice as
**indeterminate** when the worker dies, not "the ones I didn't hear about failed." Anything
already reported is a bonus, never an assumption. Practical consequence: keep worker slices
small, or the blast radius of one crash is a large re-run.

---

## Q6 — Process model

**Startup cost** (launch → connected → `initialize` answered), median of 5 launches:

| Host | Median | Range |
|---|---|---|
| xUnit v3 | 100 ms | 94–114 ms |
| TUnit | 103 ms | 98–116 ms |
| Bobcat-owned | 88 ms | 86–106 ms |

~100 ms per fresh process is **cheap enough that per-test process isolation is affordable** for
the integration tests #41 targets — those cost seconds each, so isolation is noise. It would be
too expensive to do for *every* test in a large unit-test suite, which argues for isolation being
opt-in via `[Isolated]` (as #41 already proposes) rather than a global mode.

**Cancellation works properly.** Sending the LSP-standard `$/cancelRequest` notification against
an in-flight run stopped a 30-second test after 2 seconds on all three hosts, and the run request
was answered with JSON-RPC code **−32800** (`RequestCancelled`) rather than left hanging. That is
a real, in-band cancellation path — the supervisor does not need to resort to killing processes
for timeouts.

One caveat: the cancelled test's **last reported state stays `in-progress`** — no terminal node
update arrives for it. The supervisor must synthesise the timeout/cancelled outcome itself.

**Protocol maturity — the main risk.** Server mode is undocumented in `--help`, the parameter
names are inconsistent enough to have cost me a real debugging cycle, unknown parameters fail
silently, and the handshake advertises `experimental_multiRequestSupport`. This is an
IDE-facing surface that Microsoft has not positioned as a public integration contract.

---

## Cost and risks

**Cost to adopt:** ~430 lines of client in this spike, of which the JSON-RPC transport (~230) is
generic. Call it a few days to productionise with reconnect, logging, and the count-assertion
guard.

**Risks, in order:**

1. **Undocumented, possibly unstable surface.** Mitigate by confining every wire detail behind
   one `IWorkerClient` interface. If MTP's protocol shifts — or we ever need the native-protocol
   fallback — it is one adapter, not a refactor. This is worth doing on day one regardless.
2. **Silent parameter mishandling** (Q3). Mitigate with the returned-count assertion. Non-negotiable.
3. **`experimental_multiRequestSupport: false`.** One run request at a time per connection.
   Parallelism must come from multiple worker *processes*, not concurrent requests on one
   connection. This happens to suit #41's design, but it does cap a future optimisation.
4. **Exception types absent** (Q5). Mitigate by making traits primary.
5. **Crash salvage unreliable** (Q5). Mitigate with small slices + indeterminate handling.

None of these is disqualifying, and none is improved by writing our own protocol — a native
protocol would cost strictly more (we would have to build *and* maintain adapters for xUnit and
tUnit, plus lose `dotnet test` / Test Explorer integration for free).

---

## Recommendation

**GO — adopt MTP as the #41 worker protocol.** The three load-bearing capabilities all work,
uniformly, across xUnit v3, tUnit, and a Bobcat-owned host:

- per-test structured results with a native assertion-vs-exception distinction,
- stable test identity plus selective re-run by id, in-process *and* in a fresh process,
- policy metadata (traits) available at discovery time.

Startup cost makes per-test isolation affordable, and cancellation is clean and in-band.

Two adjustments to #41 as written:

1. **Trait-driven policy becomes primary; exception-type mapping demotes to best-effort.** The
   wire does not carry exception types (Q5).
2. **The supervisor must verify a filtered run actually filtered** by asserting the returned
   outcome count. A silently-unfiltered retry is a correctness bug, not a performance one (Q3).

Suggested next step: proceed to #41 build-order step 2 (`Disposition` + `IFailurePolicy` + retry
budget), and introduce the `IWorkerClient` abstraction at the same time so no MTP detail leaks
past it.

---

## What is in this directory

| Project | Role |
|---|---|
| `Spike.Orchestrator` | The parent. JSON-RPC client, session driver, and the experiment battery. |
| `Spike.XunitV3Host` | xUnit v3 3.2.2 tests, one per outcome shape. |
| `Spike.TUnitHost` | The same shapes in TUnit 1.62.0. |
| `Spike.BobcatHost` | A Bobcat-owned MTP host — proves the *expose* half of the seam. |

Throwaway code, kept for reproducibility. It is deliberately **not** in `bobcat.sln`, so the
main build and CI never touch it.
