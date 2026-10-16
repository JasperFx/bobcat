# Bobcat on Wolverine's transport compliance suites: evaluation (bobcat#564)

**Recommendation: don't adopt yet. Adopt once #600 and #601 are fixed.** Today Bobcat cannot express
this suite's shape. The obstacle is identity, not readability: every transport collapses onto one
specification, so the one question a compliance report has to answer (which transport failed?) is
the one it can't answer. With #600 and #601 fixed, the battery is a good fit and worth adopting.
Full adoption would be a Wolverine-side follow-up issue at that point.

Measured on Bobcat at `02c3796` (0.60.4) against a reproduction of Wolverine's shape at `3eb4b6db9`.
Nothing in the Wolverine checkout was changed.

## The shape being evaluated

- `TransportCompliance<T>` (`src/Testing/Wolverine.ComplianceTests/Compliance/TransportCompliance.cs`):
  an **abstract generic base in a class library**. It references `xunit.v3.extensibility.core`,
  not `xunit.v3`, so it is not a test host. It has about 20 `[Fact]`s, several of them `virtual`,
  and its helpers (`throwOnAttempt`, `shouldSucceedOnAttempt`, `shouldMoveToErrorQueueOnAttempt`)
  are `protected`.
- **46 subclasses in 17 test projects**, all of them bodyless except one:
  `public class LightweightTcpTransportCompliance : TransportCompliance<LightweightTcpFixture>;`.
  The exception is SQLite's, which overrides one test with a `[Fact(Skip = …)]`. Everything
  transport-specific lives in the fixture (`TransportComplianceFixture`), not the test class.
- Class names repeat across projects: `BufferedSendingAndReceivingCompliance` and
  `InlineSendingAndReceivingCompliance` appear 7 times each, and
  `PrefixedSendingAndReceivingCompliance` 4 times.

`spikes/transport-compliance` reproduces that shape with no broker. `Compliance` is the library
holding the generic base. `Transports` is a test host with five subclasses: two TCP subclasses,
one SQLite subclass with a skip override and a re-specified override, and two
`BufferedSendingAndReceivingCompliance` classes in different namespaces. A fake transport that
cannot schedule a retry makes exactly one transport's `can_schedule_retry` fail.

## Step 1: binding transports and previewing

| What was tried | What happened |
|---|---|
| Step helpers on the generic base, as Wolverine writes them | **The build fails**: CS0246 `T` in `BobcatStepInterceptors.g.cs`, once per call site (42 in the spike). The interceptor's receiver is written as `TransportCompliance<T>` with no `T` in scope. **#601** |
| The same helpers made `internal` on a non-generic class, left `protected` | CS0122 in the generated file. The restriction is documented, but the failure lands in a file the author can't edit. Already **#535** |
| The vocabulary moved to a non-generic grammar class the base holds (`the.AMessageIsSent()`) | Builds, records, renders. This is the workaround the spike uses |
| `bobcat preview Transports` | **Previews nothing** ("previewed nothing (exit 0)", then the `--list-tests` output) |
| `BOBCAT_LIST_SPECS=… Transports --list-tests` | **No manifest is written.** The test compilation has no test-method syntax, so it emits no bindings |
| `[BobcatFeature("Transport compliance")]` on the base | The library now emits bindings, but keyed on `Compliance.TransportCompliance` (arity dropped): 7 identities for 35 tests, from a module initializer that `--list-tests` never runs. Still no manifest |

## Step 2: do the rendered specs read well?

**Identities don't stay distinct across subclasses.** This is the decisive finding. The run takes
the feature from `method.DeclaringType`, and for an inherited test that is the closed generic base:

```
Transport Compliance`1/can request reply                           × 5
Transport Compliance`1/can send from one node to another by …      × 5
Transport Compliance`1/can schedule retry                          × 4
Local Sqlite Backed Transport Compliance/can schedule retry        × 1   (the one override with a body)
```

So the red transport renders as:

```
failed Transports.Tcp.DurableTcpTransportCompliance.can_schedule_retry
    Feature: Transport Compliance`1
      can schedule retry FAILED
        ✓ Given the handler throws BadImageFormatException on attempt 1
        ✓ When  a message is sent
        ✗ Then  the message ends expected 'succeeded', got 'dead-lettered' on attempt expected '2', got '1'
```

The test name says DurableTcp, and the specification says only "Transport Compliance\`1". In the
results file, on the wire, on `bobcat ui` and in the ledger, which are all keyed on the identity,
five transports are one scenario recorded five times.

- **`[BobcatFeature]` on a subclass is ignored**, for the same reason, so "one feature per
  transport" can't be expressed today.
- **BOBCAT036 can't help.** The duplicates come from inheritance at run time and never exist
  within one compilation, so they collapse silently instead of being refused. Even with
  per-subclass identity, the repeated class names across projects would collide on a console that
  receives several suites. A Wolverine adoption should give each subclass a `[BobcatFeature]`
  naming its transport and mode.
- **The generic arity reaches the title** (`` Transport Compliance`1 ``).
- **A `[Fact(Skip = …)]` override of an inherited `[BobcatSpec]` errors at run time** ("has
  multiple [Fact]-derived attributes"). `[BobcatSpec(Skip = …)]` works. Wolverine has exactly one
  such override.

These are all in **#600**. The run-time half of the fix is small, and it was measured: xUnit v3
passes a `MethodInfo` whose `ReflectedType` is the concrete subclass, and `TestCase.TestClassName`
is the subclass too.

**What does read well, once identity is set aside:**

- **The shared vocabulary works.** One grammar class serves every transport. The battery's natural
  steps ("the handler throws DataMisalignedException on attempt 1", "a message is sent", "the
  message ends succeeded on attempt 3") come out as Given/When/Then without the base knowing its
  transport.
- **Gathered failures carry their cells**: `outcome: expected 'succeeded', got 'dead-lettered'`.
  That is more than Wolverine's `shouldMoveToErrorQueueOnAttempt` gives today: it throws a
  `StringWriter` dump, and it stops at the first mismatch.
- **Override semantics are sound**: an override declared on the subclass gets the subclass's
  identity, and a skipped override is withdrawn rather than reported green.

**What a conversion would have to change in Wolverine**, beyond #600 and #601:

- The `protected` helpers become `internal`/`public` (#535), or move onto a grammar object.
- **Early-return guards** (`if (Fixture.IsSenderOnlyTransport) return;`, four tests, and two more
  on `AllLocally`) record as a **passing scenario with zero steps**. That is green for a
  specification that asserted nothing, and the timing report lists it as asserting nothing. They
  should become `Assert.Skip(...)` (or `[BobcatSpec(Skip)]` overrides), which Bobcat already
  withdraws honestly.
- `TrackActivity(...).ExecuteAndWaitAsync(...)` plus `FindSingleTrackedMessageOfType` is an act
  and an assertion fused into one fluent chain. WolverineFx.Bobcat's `WhenReceived`/`ThenMessageSent`
  vocabulary is the natural target, but the compliance battery drives two hosts (sender and
  receiver) and asserts on envelope records (attempt numbers, dead-lettering, `Source`,
  correlation ids) that the scenario vocabulary doesn't cover today. Expect the base to need its
  own grammar class rather than reuse `WolverineScenario` wholesale.

## Smaller finding

**#602**: `{int}`/`{word}`/`{string}` in a step attribute render literally on a C# call site
(BOBCAT027), although `docs/marker-steps.md` says they capture. The spike uses named placeholders
(`{exception}`, `{attempt}`).

## Step 3: recommendation and what was filed

| | |
|---|---|
| **#600** | inherited specifications take the base's identity; no bindings or manifest for subclasses; subclass `[BobcatFeature]` ignored; arity in the title; `[Fact(Skip)]` over inherited `[BobcatSpec]`. **The blocker** |
| **#601** | a step helper on a generic class is CS0246 in the generated interceptor. **The second blocker**, because Wolverine's helpers live on the generic base |
| **#602** | Cucumber type placeholders vs the docs |
| #535 (existing) | `protected` helpers are CS0122 instead of a diagnostic |

Adopt once #600 and #601 land. Re-run this spike first: the expected result is one feature per
subclass (from its `[BobcatFeature]`, or its class name), five distinct identities per inherited
test, a manifest naming every one, and `bobcat preview` listing them. Then open the Wolverine
adoption issue. A sensible pilot is the two TCP suites, `LocalPostgresqlBackedTransportCompliance`
and one broker, with `[BobcatFeature]` per subclass and `Assert.Skip` in place of the early
returns.

## Reproducing

```bash
dotnet build spikes/transport-compliance/TransportComplianceSpike.slnx
H=spikes/transport-compliance/Transports/bin/Debug/net10.0/Transports
BOBCAT_MONITOR=0 BOBCAT_RESULTS=/tmp/tc $H          # the identities, in /tmp/tc/*.json
BOBCAT_LIST_SPECS=/tmp/tc.json $H --list-tests      # no file written today
dotnet run --project src/Bobcat.Console -- preview spikes/transport-compliance/Transports
```

To see #601, move `ComplianceSteps`' four methods back onto `TransportCompliance<T>`.
