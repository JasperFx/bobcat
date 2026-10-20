# Test Automation Strategy Recommendations

**What this page is:** the *why* behind the other tutorials. It is a set of recommendations for a test
automation strategy that holds up on a real team, and for each one, where Bobcat does the job. The
other tutorials are the *how*. This page links to them rather than repeating them.

The recommendations come from two posts of mine, restated here in the present tense and
updated where Bobcat now does what the posts describe doing by hand:

- [My Recommendations for a Test Automation Strategy](https://jeremydmiller.com/2024/09/29/my-recommendations-for-a-test-automation-strategy/) (2024)
- [Succeeding with Automated Integration Tests](https://jeremydmiller.com/2015/06/25/succeeding_with_integration_testing/) (2015)

The running example is
[the BankAccountES sample](https://github.com/JasperFx/wolverine/tree/main/src/Samples/Bobcat/BankAccountES) (in the Wolverine repository since Bobcat 1.0.0-rc.2), a
Wolverine bank account application on Marten or Fisher, and its rule that **a withdrawal may not
overdraw the account**.

## What a strategy should give you

A test automation strategy succeeds when the suite gives you four things. Coverage is not one of
them: a coverage number tells you how much code ran, not whether it's safe to ship.

### 1. Confidence to ship

A passing suite should mean the change is safe to release. If a team still runs a manual regression
pass "to be sure", the suite isn't doing its job. Track the defects that escape to production. They
tell you where the suite is thin better than any metric does.

Two things in Bobcat protect what a green build means:

- **A spec that isn't written yet can't make the build red, and can't pass either.** A
  `[BobcatSpec(…, Pending = true)]` stub is *skipped* and reported as skipped, never run with its
  failure absorbed. It still sits on its Event Model slice as an open question
  ([Stub-first](../marker-steps.md#bobcatspec)).
- **A spec that runs ahead of its feature doesn't turn the build red either.**
  `Lifecycle = Lifecycle.Acceptance` runs it every time and records every failing step under its own
  `acceptance: failing` verdict, while the test platform reports it skipped. A passing one says it is
  ready to promote, and `BOBCAT_LIFECYCLE=strict` makes its failures real on a branch that claims to
  be done ([Not yet passing](../marker-steps.md#acceptance) in Writing Specifications).
- **You can see what is unspecified.** A spec bound to its slice puts the Event Model and the run
  evidence side by side: `bobcat slice-coverage` lists every slice with no spec (`no-spec`) or with a
  spec that no run has exercised (`no-evidence`). See
  [Querying Runs from the Command Line](../agent-queries.md) and
  [Event Modeling and Spec Driven Development](event-modeling.md).

### 2. Easy to write tests

A test has to be cheap to write, or nobody writes the second one. What makes it cheap is a
**declarative** style: the test names the business situation and leaves the mechanics to helpers.
FIT, FitNesse and Gherkin did this with an external language. Bobcat does it with an internal DSL,
which is ordinary C# methods:

- **Step helpers are the DSL.** A `[Given]`/`[When]`/`[Then]` attribute on a helper makes every call
  to it a rendered step ([Step helpers](../marker-steps.md#step-helpers)).
- **The Critter Stack vocabulary is already written**, in WolverineFx.Bobcat
  ([Testing with Bobcat](https://wolverinefx.net/guide/testing-with-bobcat), in the Wolverine docs).
- **Tables and partial objects** for data-heavy rules: a spec names only the members it is about
  ([Data Intensive Specifications](data-intensive-specifications.md),
  [Expected Objects](../expected-objects.md)).
- **A board writes your first specs.** A Wolverine-side command turns each example on an eventmodelers.ai
  board into a spec bound to its slice (it was `bobcat import-event-model --specs` before Bobcat 1.0.0-rc.2;
  `import-event-model` still writes the stubs and the definition, see [`import-event-model`](../bobcat-tool.md#import-event-model)).

Keep steps declarative: `WhenPosted(new WithdrawFunds(accountId, 200), …)` and not ten lines building
an HTTP request. The mechanics belong in a helper the next test reuses.

### 3. Fast enough feedback

Tests should run fast enough that developers run them all the time, so a regression shows up close to
the change that caused it. Bobcat's part:

- **`bobcat watch`** reruns the spec project on every change, and `--feature` / `--scenario` narrow
  it to what you're working on ([The `bobcat` tool](../bobcat-tool.md#run-preview-pick-watch)).
- **A warm resident runner** keeps the test host's process alive between runs, so a rerun skips
  starting the CLR ([The Resident Runner](../resident-runner.md),
  [Warm Runs](../warm-projected-runs.md)).
- **The timing report** shows where a run spent its time: the slowest steps, the gaps between them,
  and scenarios that assert nothing ([Where the time went](../marker-steps.md#timing)). Under the
  supervisor, `RunTiming` reports each test's share of the wall clock.
- **Rerun only what is red.** `bobcat run --failed` runs the specifications that failed last time, by
  identity, and `--repeat` replays the last run's selection
  ([Rerunning what failed](../bobcat-tool.md#failed-repeat)).
- **Run one slice.** `--slice`, `--chapter`, `--domain` and `--pattern` narrow a run to a corner of
  the Event Model ([Selecting by Event Model coordinates](../bobcat-tool.md#selecting-by-coordinates)).
- **The same loop, for a coding agent.** A failure that says what the system did, read as JSON from
  the results file or a `bobcat failing-spec` query, and a rerun of only what was red, is how an agent
  gets from red to green without a person reading the output for it
  ([Agent Friendly Integration Tests](agent-friendly-tests.md)).

### 4. Reliability

A result you can't trust is worse than no result, because people learn to ignore red. **If a flaky
test can't be fixed or rewritten, delete it.** A reliable suite with less coverage is more useful
than a broad one nobody believes.

**Retry is a last resort**, the 2015 post's rule, and Bobcat is built that way:

- **Retries are opt-in.** A test with no `Retry` trait is never retried, even when a budget is
  configured. When a test does pass on a retry, that is reported as `PassOnRetry`, never folded into
  the clean passes ([Reliable Integration Testing](reliable-integration-testing.md#step-4-report-flakiness-instead-of-hiding-it)).
- **Flakiness has a memory.** The ledger keeps every attempt across runs, so "this test is
  flaky" is a claim with evidence behind it.
- **A hang is reported, not just suffered.** Stall detection names the test that stopped making
  progress, and can kill and retry it if you opt in
  ([When a parallel run hangs](../parallel-ready-suites.md#when-a-parallel-run-hangs-bloats-or-is-cut-short)).
- **Broken infrastructure stops the run.** A `SpecCatastrophicException` (the database is gone, the
  host won't start) is never retried. In process, every specification after it fails at once with
  `SpecEnvironmentLostException` naming the cause, instead of failing one timeout at a time; under the
  supervisor it aborts the run ([When the environment is gone, stop](../marker-steps.md#environment-lost)).

Most flaky tests against an asynchronous system come from **asserting before the system is done**,
then papering over it with a `Thread.Sleep`. Wait for the work instead of for the clock:

- On the Critter Stack, WolverineFx.Bobcat's steps wait for the async projections and for every
  message a tracked-session act caused ([Testing with Bobcat](https://wolverinefx.net/guide/testing-with-bobcat), in the Wolverine docs).
- `SpecAssert.EventuallyAsync` polls anything else, and records one cell saying how long it took
  ([Waiting for an answer that arrives later](../marker-steps.md#waiting-for-an-answer-that-arrives-later)).

When a test against an asynchronous system does fail, it should say what the system did, not only
that it ended up wrong: a failure nobody can diagnose gets retried until it passes, and becomes the
flaky test you were trying to avoid. Put the system's own telemetry into the specification's
output, written only when it fails: [Custom Telemetry on Failure](../failure-telemetry.md), worked
through on Wolverine's leadership-election suite.

## How to get there

### 5. Test with the finest-grained mechanism that tells you something important

This is my Only Rule of Testing, and it carries more weight than the rest of this page. Each
step up from a pure function adds infrastructure: a store, a message bus, an HTTP pipeline, a
browser. Each one makes a test slower to run, harder to write and more likely to fail for a reason
that has nothing to do with the behaviour. So:

- **Prefer whitebox to blackbox.** A test that reaches into the application (a handler, a service, the
  store) is faster and more focused than one that only sees the outside. Blackbox tests are for
  proving the technical integration, not for proving business rules.
- **Prefer the service layer to the UI, and HTTP to the browser.** Calling an endpoint in process
  through [Alba](https://jasperfx.github.io/alba/) proves everything a browser test of the same
  request would, apart from the page itself.
- **When a coarse test is hard to debug, add a finer one under it** rather than another coarse one.

In Bobcat terms, pick the lowest of these that proves the behaviour (the Critter Stack rows are
documented in [Testing with Bobcat](https://wolverinefx.net/guide/testing-with-bobcat)):

| Layer | Vocabulary | Proves |
|---|---|---|
| A pure function | a plain xUnit / TUnit test, no Bobcat needed | the rule itself |
| The store | `CritterStackSpec` (WolverineFx.Bobcat) | events in, aggregates and read models out, refusals |
| A message | `WolverineSpec` (WolverineFx.Bobcat) | the handler, its middleware, what it cascaded |
| HTTP | `WolverineHttpSpec`, or Alba in your own base class | routing, binding, validation, status codes |
| The browser | Playwright | what is genuinely UI |

**The overdraft rule, layer by layer.** In BankAccountES the rule is `WithdrawFundsEndpoint.Validate`,
a pure function of the command and the loaded account. That makes the finest grain a plain unit test,
with no host and no database:

```cs
[Fact]
public void a_withdrawal_larger_than_the_balance_is_refused()
{
    var account = new Account { Balance = 100m };

    var problems = WithdrawFundsEndpoint.Validate(new WithdrawFunds(Guid.NewGuid(), 200m), account);

    Assert.Equal(400, problems.Status);
}

[Fact]
public void a_withdrawal_of_the_whole_balance_is_allowed()
{
    var account = new Account { Balance = 100m };

    var problems = WithdrawFundsEndpoint.Validate(new WithdrawFunds(Guid.NewGuid(), 100m), account);

    Assert.Same(WolverineContinue.NoProblems, problems);
}
```

The boundary cases (exactly the balance, a penny over) belong here, where each one costs
microseconds.

**The wiring is a separate claim**, and it needs one specification at the layer where the rule is
reached. In this application that's HTTP, because Wolverine.HTTP runs `Validate` before the endpoint.
One spec proves the request is refused, nothing is appended, and the balance is unchanged:

```cs
[BobcatSpec(typeof(WithdrawFunds), Domain = "Banking")]
public async Task withdrawing_more_than_the_balance_leaves_the_account_untouched()
{
    var accountId = Guid.NewGuid();
    await GivenEvents<Account>(accountId,
        new AccountOpened(accountId, Guid.NewGuid(), "USD"),
        new FundsDeposited(accountId, 100, 100));

    await WhenPosted(new WithdrawFunds(accountId, 200), "/api/accounts/{accountId}/withdrawals", accountId);

    ThenTheResponseStatusIs(400);
    ThenNoEvents();
    await ThenAggregate<Account>(accountId, Specify<Account>().With(x => x.Balance, 100m));
}
```

There's one HTTP spec for the refusal and one for the
[happy path](https://github.com/JasperFx/wolverine/blob/main/src/Samples/Bobcat/BankAccountES/Tests/BankAccountEventSourcing.cs),
not one per boundary case. The spec runs the whole pipeline in process through Alba, against Marten
or Fisher, without a browser.

If the same rule lived in a message handler, the middle layer would carry it: `WhenReceived(new
WithdrawFunds(…))` followed by `ThenRefusedWith(…)` and `ThenNoEvents()`, with no HTTP at all.
[`FreezeAccountSpecs`](https://github.com/JasperFx/wolverine/blob/main/src/Samples/Bobcat/BankAccountES/Tests/FreezeAccountSpecs.cs)
is that layer in the same sample, for a command that succeeds. A store-only `CritterStackSpec` is the layer below it, for
applications with no Wolverine ([The store-only act](https://wolverinefx.net/guide/testing-with-bobcat#the-store-only-act)).

**The browser is for what only a browser can show:** a page renders the refusal, a button is
disabled, a flow spans screens. When you do need Playwright, use the **driver pattern**. Page
mechanics (selectors, clicks, waits) go in a driver class, and the spec's `[When]` / `[Then]` helpers
call the driver in business language: "When Ann withdraws $200", not "click `#withdraw`, fill
`#amount`". The driver is where a redesigned page gets fixed once. The specs keep reading the same,
and their steps render the same way as at every other layer.

Bobcat's [comparison with other BDD tools](https://github.com/JasperFx/bobcat/blob/main/design/bdd-tools-comparison.md)
(item C16) is where this guidance was decided.

### 6. Test automation engineers belong on the development team

Most failed test automation efforts trace back to developers and testers working apart: one group
writes the system, the other writes tests against it from outside and can't change it to make it
testable. Optimize the whole. Developers help with testing, testers work in the code, and either can
change the architecture when testing calls for it.

Bobcat specs help because there's one artifact for both groups. A spec is an ordinary test in the
same solution, which a developer can debug, and it renders as Given/When/Then that a tester or
analyst can read and review ([Reading the specification the run produced](../marker-steps.md#reading-the-specification-the-run-produced)).

### 7. Same repository, same language

Test code lives beside the application code, in the same repository and the same language
(preferably the server-side one). A separate test repository drifts out of step with the code it
tests, and a separate language means the people who change the code can't fix the tests.

Bobcat specs are C# tests in a project next to the application, on the test framework you already
use: xUnit v3, TUnit, NUnit or MSTest ([Specifications with Code](specifications-with-code.md)).
There is no second file and no binding layer to keep in step.

### 8. Easy to run locally, on demand

Any developer should be able to run any part of the suite against their own branch, whenever they
want. Then failures get diagnosed without waiting for a build server, a debugger can be attached, the
people who break tests keep them current, and a big refactoring has a safety net. **A suite that
only runs on a CI server almost never succeeds.**

- `dotnet test`, or `bobcat run` for the rendered specification tree
  ([Getting Started](../getting-started.md#_4-run-it)).
- `docker compose up -d` for the infrastructure. BankAccountES runs on the samples' Postgres.
- **Fisher, on a SQLite file, is the zero-infrastructure inner loop.** `EventStore=Fisher dotnet test`
  runs the same BankAccountES specs with nothing installed, because only `Program.cs` names the
  store ([Testing with Bobcat](https://wolverinefx.net/guide/testing-with-bobcat)).
- A spec is an ordinary test, so the IDE runs and debugs it like any other
  ([Integrating Bobcat with Your IDE](ide-integration.md)).

### 9. Self-contained tests

Each test sets up the state it needs, inside the test. Then the system is in exactly the state the
test expects, and a reader sees the inputs, the action and the assertions together instead of
hunting through a shared data script.

Set the state up **through the application's own model, declaratively and tersely**. Don't build a
"shadow data access" layer, and don't write gobs of SQL coupled to today's schema.

- `GivenEvents<Account>(accountId, new AccountOpened(…), new FundsDeposited(…))` arranges a stream
  in the domain's own language, as in the overdraft spec above.
- `Specify<T>()` builds an object from only the members the test cares about. This is the test data
  builder from the 2015 post's comments. What a test doesn't say comes from **declared defaults**,
  registered once for a project or per spec (`PartialObjects.UnspecifiedValues`, or a spec's
  `UnspecifiedValues`):

<<< ../../src/Bobcat.Tests/Partial/DeclaredDefaultsTests.cs#declared-defaults{cs}

- **A spec's events are its own.** A store-only act keeps the events on the streams the spec arranged
  or started, and sets other tests' appends aside as foreign. A whole-store assertion that fails while
  foreign events were seen says "cross-talk suspected" instead of blaming your code
  ([The store-only act](https://wolverinefx.net/guide/testing-with-bobcat#the-store-only-act)).

### 10. No shared databases

A database shared between developers, testers or test environments gives false failures and false
passes. Every person and every environment gets its own, which means database setup and teardown have
to be fully scripted. Marten was built partly to make that cheap.

- The Critter Stack stores build their own schema, and BankAccountES resets its stores when the host
  boots (`ResetEventStoresAsync`).
- Run in parallel worker processes, **each worker gets its own database** through
  `MtpWorkerFactory.EnvironmentFor`, keyed by lane ([Reliable Integration Testing](reliable-integration-testing.md#step-2-isolate-resources-per-worker)).
- When two tests do collide on one store, the failure says "cross-talk suspected" instead of
  pointing at the wrong code (item 9).

### 11. Prefer technologies with a strong local story

Choose infrastructure a developer can run on a laptop: Postgres and Marten in Docker, Fisher on a
file, RabbitMQ in a container. What you can run locally, you can reset, inspect and debug.

When a cloud-only service is unavoidable (Azure Service Bus, say), give each developer and each test
environment their own namespace, or a prefix on every resource name. The goal is predictable inputs:
a test can only expect an outcome if nobody else is feeding the same queue.

### 12. Collapse a distributed system into one process for testing

Run the test harness, the web server and the database client in one process, so a test can start the
system, drive it and observe it without deployments, ports or timing between processes. Replace
dependencies that are hard to control (a third-party API, a payment gateway) with stubs, even in
functional tests. Register the stubs in the container.

Wolverine's in-process tracked sessions are the modern answer. A `WolverineSpec` act runs the
message, every handler it cascades to and every local queue it touches in the test's own process, and
waits for all of it to settle before the first `Then`
([Act](https://wolverinefx.net/guide/testing-with-bobcat#act)). BankAccountES's host is one `AlbaHost.For<Program>()` for
the whole run.

### 13. Continuous integration: fast tests and slow tests

Tests nobody runs go stale, and failures found late are hard to trace because the list of suspect
changes keeps growing. Run everything in CI, the slow end-to-end tests included. When the whole suite
is too slow for every push, split it: a main build runs the fast tests, and a second build runs the
slow ones once the first is green.

- [Integrating Bobcat with CI](continuous-integration.md) covers running, filtering and segmenting the
  suite.
- The supervisor splits a slow suite across worker processes by test class
  ([Reliable Integration Testing](reliable-integration-testing.md)).
- **Keep the JSON results file.** `BOBCAT_RESULTS` writes every scenario's steps, cells and tables,
  with a JSON Schema, for CI to archive ([The results file](../bobcat-tool.md#results-file)), and
  `bobcat results-html` turns it into one self-contained page a person opens from the artifact
  ([The HTML results page](../bobcat-tool.md#results-page)).

### 14. Testability is an architectural concern

Weigh testability when you choose technologies and design the system, the same as performance or
security. Compose the application with an IoC container so a test can swap a dependency, keep business
rules out of the database, and **add hooks purely so tests can observe and control the
application**.

- **Time.** Read the clock through `TimeProvider`, and a spec can freeze it and move it forward
  (`GivenTheDateIs`, `WhenTheClockAdvancesBy`, over `BobcatClock`). A rule about "after 30 days"
  doesn't have to wait 30 days.
- **Asynchronous work.** Tracked sessions and correlation ids let a test know when the system is done
  (item 4). Without them a test can only guess.
- **The model.** Binding specs to Event Model slices (`[BobcatSpec(typeof(WithdrawFunds))]`) makes
  what the system does, and what has been specified, something you can query
  ([Binding a specification to a slice](../marker-steps.md#bobcatslice)).

In BankAccountES the overdraft rule is a pure `Validate` function rather than code buried inside the
endpoint. That's why item 5's unit test exists, and why it needs nothing.

## Where to go next

- Writing the first specification -- [Specifications with Code](specifications-with-code.md)
- A slow or flaky suite -- [Reliable Integration Testing](reliable-integration-testing.md)
- The pipeline -- [Integrating Bobcat with CI](continuous-integration.md)
- Failures an agent can act on -- [Agent Friendly Integration Tests](agent-friendly-tests.md)
