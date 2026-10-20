# Test Automation Strategy Recommendations

Bobcat is the result of over 25 years of oftentimes frustrating test automation work and some lessons learned from using different approaches
to the old Extreme Programming dream of "[executable specifications](https://en.wikipedia.org/wiki/Specification_by_example)" where requirements
are written in a format that is both human-readable for domain expert review and ultimately executable as automated tests
during actual development. This tutorial tries to explain our guiding philosophy for creating effective automated tests.

The running example is
[the BankAccountES sample](https://github.com/JasperFx/wolverine/tree/main/src/Samples/Bobcat/BankAccountES) (in the Wolverine repository since Bobcat 1.0.0-rc.2), a
Wolverine bank account application on Marten or Fisher, and its rule that **a withdrawal may not
overdraw the account**.

## What a strategy should give you

We think that a test automation strategy succeeds when the suite gives you five things.

::: tip
We're not terribly concerned with test coverage as an official metric but instead striving
for a feedback mechanism using automated tests that's good enough to feel confident shipping changes.
:::

### 1. Confidence to ship

::: info
These `Acceptance` and `Regression` life cycle designations are drawn from experiences
with Extreme Programming projects in the mid 2000's. This was originally part of the notion
that developers would hopefully receive "executable specifications" as the detailed requirements. Our hope for Bobcat
is that this fits well into newer ideas of "Spec Driven Development."
:::

A passing suite should mean the change is safe to release. If a team still runs a manual regression
pass "to be sure", the suite isn't quite doing its job yet.

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
- **If you're using Event Modeling with Bobcat and the rest of the Critter Stack, you can see what is unspecified.** A spec bound to its slice puts the Event Model and the run
  evidence side by side: `bobcat slice-coverage` lists every slice with no spec (`no-spec`) or with a
  spec that no run has exercised (`no-evidence`). See
  [Querying Runs from the Command Line](../agent-queries.md) and
  [Event Modeling and Spec Driven Development](event-modeling.md).

In a perfect world, you have detailed test cases in the form of specifications that you can run in a tight loop
to "know" whether your new feature or the changes you just made are exactly correct.

::: tip
To be perfectly clear, Bobcat is perfectly usable without also using any of the rest of the Critter Stack -- but we have specific
integrations and support for Bobcat's other Critter Stack siblings like [Wolverine](https://wolverinefx.io).
:::

### 2. Easy to write tests

::: info
Bobcat was originally going to support Gherkin as an alternative mechanism for expressing
tests (but with much more focus on table-driven testing than other Gherkin tools), but the experimentation with the current "projected" model 
went well enough that the community decided to simplify the tool and focus strictly on its current model. 

We also briefly considered a more typical .NET "Internal DSL" approach, but that's
already been tried by many other tools as is, and we didn't think that approach added enough value.
:::

A test has to be cheap to write. You can reduce the mechanical cost of building tests by investing in
a **declarative** style: the content of the test clearly describes the business rules and expected outcomes, then leaves the mechanics to helpers.
FIT, FitNesse and Gherkin did this with an external language. Bobcat does it with just ordinary C# or F# methods:

- **Step helpers are the DSL.** A `[Given]`/`[When]`/`[Then]` attribute on a helper makes every call
  to it a rendered step ([Step helpers](../marker-steps.md#step-helpers)).
- **The Critter Stack vocabulary is already written**, in WolverineFx.Bobcat
  ([Testing with Bobcat](https://wolverinefx.net/guide/testing-with-bobcat), in the Wolverine docs).
- **A board writes your first specs.** A Wolverine-side command turns each example on an eventmodelers.ai
  board into a spec bound to its slice (it was `bobcat import-event-model --specs` before Bobcat 1.0.0-rc.2;
  `import-event-model` still writes the stubs and the definition, see [`import-event-model`](../bobcat-tool.md#import-event-model)).

Keep steps declarative: `WhenPosted(new WithdrawFunds(accountId, 200), …)` and not ten lines building
an HTTP request. The mechanics belong in a helper the next test reuses.

**Test data setup is where most of the writing cost goes.** Jeremy's opinions on it, written down in
2013 in [My Opinions on Data Setup for Functional Tests](https://jeremydmiller.com/2013/01/26/my-opinions-on-data-setup-for-functional-tests/),
haven't changed:

- **Express only the data that is germane to the test.** A test about the overdraft rule names the
  balance and the withdrawal. It should not have to say the account holder's name, the branch or the
  date the account was opened, and a reader should not have to work out which of fifteen columns
  actually mattered. Everything the test does not say comes from a sensible default behind the scenes,
  which is also what makes each new test cheap to write.
- **Decouple the input from the implementation details.** Write the setup as sentences or logical
  tables in the domain's terms, never as SQL, JSON or raw API calls tied to today's storage (if you can help it). When a
  column is added, you add a default for it and no test changes. That is reversibility: the tests make
  the system easier to change rather than harder.
- **Go in through the front door.** Set the state up with the services the application itself uses to
  persist data. A backdoor write to the database duplicates the production logic and can put the system
  into a state production never reaches. The 2013 post's example was a team whose Ruby ActiveRecord
  setup scripts broke on every schema change in the C# backend, because they were a second copy of it.
  More on this under [Self-contained tests](#_10-self-contained-tests).

In Bobcat the first two rules are the **partial object**. `Specify<T>()` names only the members the test
is about, and the fill policy supplies the rest, with a declared default for a value that is "always
this unless the test says otherwise"
([Expected Objects](../expected-objects.md#a-partial-object-names-what-the-specification-is-about)):

```csharp
await GivenEvents<Account>(accountId,
    Specify<AccountOpened>().With(x => x.AccountId, accountId),
    Specify<FundsDeposited>().With(x => x.Amount, 100m));
```

The same partial object builds a command, an arranged event or an expectation, and written as a
`| Property | Value |` table it leaves a blank cell unspecified, so one table can set up objects of
several types ([Data Intensive Specifications](data-intensive-specifications.md)).

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

**Retry is a last resort**, the 2015 post's rule, and Bobcat is built that way. The
[Bobcat Supervisor](https://jasperfx.net/news/introducing-bobcat-supervisor) is the part that runs a
large suite across worker processes and applies that rule at the only altitude that can act on it:
it retries *selectively*, only the tests that opted in and only within a budget; it can restart a
wedged or crashed worker process, or recycle a Docker container, before a retry; and it collects
what it saw about flaky tests across runs, so flakiness becomes a measured rate instead of an
impression. It runs Wolverine's own CI, and it needs no change to the tests, because it drives the
compiled test host through Microsoft.Testing.Platform and never loads the test assembly.

- **Retries are opt-in.** A test with no `Retry` trait is never retried, even when a budget is
  configured. A recovery hint narrows it further, to one failure class and what fixes it: retry
  in a fresh process for this exception, recycle these containers for that one, never retry this
  deterministic bug. When a test does pass on a retry, that is reported as `PassOnRetry`, never
  folded into the clean passes ([Reliable Integration Testing](reliable-integration-testing.md#step-4-report-flakiness-instead-of-hiding-it)).
- **Flakiness has a memory.** The ledger keeps every attempt across runs, so "this test is
  flaky" is a claim with evidence behind it, and the hints it proposes are text a person accepts
  by writing into the code. Nothing reads them back into a policy on its own.
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

Nobody trusts flaky tests, to the point where they become an albatross around a team's neck: the test code
still has to evolve with the rest of the system even though it isn't providing any value. And our new AI-centric way
of working is leading to more ambitious and longer-running Continuous Integration runs, so there's no point in
making that problem worse by executing tests you don't trust anyway!

### 5. Failures that explain why and how

::: tip
Sorry to break this to you, but your automated tests will fail from time to time -- and hopefully just because the actual code
is broken! Especially with data intensive scenarios or any kind of asynchronous processing or *shudder* user interface testing, it's
going to be essential to add more contextual information to the output of a test failure to have any reasonable chance of
being able to fix the failure.
:::

`Expected 100, got 200` tells you that a test failed. It doesn't tell you *why* the system answered
200, or *how* it got there, so the next step is a rerun under a debugger, and after that a guess.
The harness should hand over the whole story the first time: which inputs were given, every claim
that was wrong rather than the first one that threw, which rule refused the request, and what the
system did on the way. Collecting that costs a passing test nothing when it is written out only on
failure, and it is what makes a red build something a person, or a coding agent, can act on without
reproducing it.

What Bobcat writes out for a failed specification:

- **Every disagreement, not just the first.** `SpecAssert` and the `Then` steps gather each wrong cell
  and the specification keeps running, so a failure says "the balance is wrong *and* no email was
  sent", which is a different diagnosis than either on its own
  ([gather every disagreement before anything throws](agent-friendly-tests.md#step-1-gather-every-disagreement-before-anything-throws)).
- **A collection failure says which rows, and how.** A set verification compares the rows the system
  produced against the rows the specification describes, matched by the columns that identify them,
  and writes the whole comparison out as one table. A row that matched but disagrees on a cell is
  `FAIL` with the expected and actual value in that cell; a row the specification describes and the
  system never produced is `MISSING`, shown with the values it expected; a row the system produced
  that the specification doesn't describe is `EXTRA`; and when order was part of the claim, a row
  that arrived in the wrong place is `ORDER`. Shouldly's `ShouldBe` on a list says "the collections
  differ"; this says "the Sweatpants line is missing and a Shirts line turned up instead"
  ([Set verification](data-intensive-specifications.md#set-verification)). The same comparison runs
  inside an expected object, so a collection member reports its items the same way
  ([A collection member is a set](../expected-objects.md#a-collection-member-is-a-set)).
- **A validation rule that refused the request is named.** `ThenRefusedWith("Insufficient funds")`
  checks the endpoint's ProblemDetails or the exception the handler threw, and
  `ThenRefusedWithProblem(Specify<ProblemDetails>().With(x => x.Detail, …))` compares the problem
  member by member. When the act was *not* refused, the step says so. When it was refused for another
  reason, the cell shows the refusal the system actually gave beside the one the spec expected, and
  the HTTP table below it shows the status and body the endpoint answered with.
- **The HTTP history.** Every exchange a scenario made, with the verb, the route, the status and the
  request and response bodies, as a table under the steps; a 5xx row is red inside it.
- **The messaging history.** For a Wolverine act, the tracked session's record of every envelope in
  order: when it happened on the session's clock, what was sent or received, to which destination, on
  which attempt, and a red row for an execution that failed or an envelope that went to the error
  queue. **The events appended** by the act are a table of their own, so a `ThenEvents` that asked
  about one event sits beside everything the stream actually gained
  ([Testing with Bobcat](https://wolverinefx.net/guide/testing-with-bobcat), in the Wolverine docs).
- **Logs correlated to the step.** `SpecLog.Write` puts a line under the step that was running when it
  was written, so a wait reports what it saw each time the answer changed and an act reports what it
  did, in the order the specification reads
  ([Saying what the system did](../marker-steps.md#step-logs)). Capturing the application's own
  `ILogger` output into the same place is on the list for after 1.0.
- **Your own telemetry.** A `TableReport` of what *your* system did during the scenario (a cluster's
  assignment timeline, say, or a saga's state changes), and a transcript or a screenshot as an
  attachment when it is too long to read inline
  ([Custom Telemetry on Failure](../failure-telemetry.md),
  [Failure evidence](../marker-steps.md#attachments)). The one rule that catches every producer: the
  telemetry has to reach the specification from the test's own thread, not from the system's
  background loops ([where the telemetry comes from matters](../failure-telemetry.md#where-the-telemetry-comes-from-matters)).
- **Bounded and machine-readable.** Logs, reports and attachments are capped with a count of what was
  left out, never silently truncated, and all of it lands in the JSON results file and the
  `bobcat failing-spec` query as well as on the console, so an agent reads the same story a person
  does ([Agent Friendly Integration Tests](agent-friendly-tests.md)).

Wolverine's leadership-election suite is the worked example: a failure that used to be a
`TimeoutException` with two lists of node names now carries the timeline of every agent assignment
the cluster made, which node held each agent at the end, and the full transcript as an attachment
([Custom Telemetry on Failure](../failure-telemetry.md#worked-example-wolverine-s-leadership-election-compliance-suite)).

## How to get there

### 6. Test with the finest-grained mechanism that tells you something important

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

### 7. Test automation engineers belong on the development team

Most failed test automation efforts trace back to developers and testers working apart: one group
writes the system, the other writes tests against it from outside and can't change it to make it
testable. Optimize the whole. Developers help with testing and testers work in the code.

Bobcat specs help because there's one artifact for both groups. A spec is an ordinary test in the
same solution, which a developer can debug, and it renders as Given/When/Then that a tester or
analyst can read and review ([Reading the specification the run produced](../marker-steps.md#reading-the-specification-the-run-produced)).

### 8. Same repository, same language

Test code lives beside the application code, in the same repository and the same language
(preferably the server-side one). A separate test repository drifts out of step with the code it
tests, and a separate language means the people who change the code can't fix the tests.

Bobcat specs are C# tests in a project next to the application, on the test framework you already
use: xUnit v3, TUnit, NUnit or MSTest ([Specifications with Code](specifications-with-code.md)).
There is no second file and no binding layer to keep in step.

### 9. Easy to run locally, on demand

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

### 10. Self-contained tests

**Every part of a test belongs in one place: the inputs, the action and the expected outcome.** A
reader should be able to open one specification and see cause and effect together, with nothing to
look up in a shared data script, a fixture class three files away or a database somebody loaded last
week. The 2013 post made this its own rule, and it does two different jobs:

- **It is easier to reason about.** The question a red test asks is "given *these* inputs, why *this*
  outcome?". When the inputs are in the test, the diagnosis starts from a complete picture. When they
  are in a shared setup, the first job is working out which of its rows this test actually depended
  on, and the second is finding who changed one of them.
- **It is far more reliable.** A test that owns its state does not depend on which test ran before
  it, on another test having left a row behind, or on a shared data set nobody dares to change
  because some test somewhere needs it as it is. It gives the same answer run alone, run in a
  different order, or run in parallel with everything else. That is also what makes
  `bobcat run --failed` and running one spec by identity mean anything: a test that only passes
  after its neighbours cannot be rerun on its own.

Shared setup is for the static things only: lookup data, a default user, reference tables. Copy and
paste the rest between tests rather than share it. The duplication is cheap when a partial object
names only what matters, and the isolation is worth more than the DRY.

Each test therefore sets up the state it needs, inside the test, **through the application's own
model, declaratively and tersely**, which is the 2013 post's "go in through the front door" from
[Easy to write tests](#_2-easy-to-write-tests). Don't build a "shadow data access" layer, and don't
write gobs of SQL coupled to today's schema.

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

### 11. No shared databases, schemas or brokers

A test can only expect an outcome if nobody else is feeding its inputs. A database, a schema or a
queue shared between developers, between test environments, or between tests running at the same
time gives false failures (someone else's data is in the way) and false passes (someone else's side
effect just happened to satisfy your assertion). So nothing the tests write to is shared, at any of four levels,
and setup and teardown are fully scripted at every one of them. [Marten](https://martendb.io) was built partly to make that
cheap, and the Critter Stack stores all build their own schema from nothing.

**Per developer and per environment.** Every person, every branch build and every CI job gets its
own database. BankAccountES resets its stores when the host boots (`ResetEventStoresAsync`), so a run
starts from a known state instead of from whatever the last one left. Clean up at the *start* of a
run, not the end: a run that was killed never reaches its teardown.

**Per test class.** A test class is the unit every framework's isolation contract is written against,
and the unit Bobcat's supervisor partitions by, so it is the natural owner of a schema. Marten's
`DatabaseSchemaName` gives a class its own schema in one shared database; a database per fixture on
one Postgres container is the same idea one step up, and Testcontainers makes it a few lines.

**Per worker process.** Run in parallel, two classes that share a store land in different processes,
so each worker gets its own database. `LaneDatabases` drops and recreates one per lane on PostgreSQL
or SQL Server, and `WorkerConnectionStrings` hands each worker a whole connection string under the
.NET Aspire convention, so the suite reads it the way the application does. Brokers get the same
treatment, a complete connection string per lane, never a host or vhost fragment to stitch on. A
suite that already starts its own container per process has this for free
([Isolate resources per worker](reliable-integration-testing.md#step-2-isolate-resources-per-worker),
[per-worker resource isolation](../parallel-ready-suites.md#step-2-per-worker-resource-isolation)).

**Per test.** Inside one process, prefer a fresh identity per test (a new stream id, a new order
number) over wiping the store between tests. Wiping serializes the suite, and a fresh id costs nothing.
A store-only act goes further: it keeps the events on the streams the spec arranged or started, and a
whole-store assertion that fails while foreign events were seen says "cross-talk suspected" instead
of pointing at the wrong code (item 10).

**Message brokers** deserve their own paragraph, because the cheapest isolation is not to need one:

- **In process first.** `DisableAllExternalWolverineTransports()` makes Wolverine discard every send
  to an external broker while the tracked session still records it, so a spec can assert
  `ThenMessageSent(…)` with no broker running at all; `RunWolverineInSoloMode()` turns off the
  durability agents and leader election the test doesn't need
  ([Integration testing](https://wolverinefx.net/guide/http/integration-testing), in the Wolverine docs).
  Most specs want exactly this.
- **When a spec is about the broker**, give it a broker nobody else is on: a virtual host per suite
  or per fixture on RabbitMQ, a namespace or a name prefix per developer and per environment on a
  cloud broker such as Azure Service Bus, and `AutoProvision().AutoPurgeOnStartup()` so every queue
  starts empty. The durable inbox and outbox tables live in the lane's own database, so they come
  isolated with it.
- **Readiness is checked once, up front.** `DockerComposeResource` starts the containers and reads
  their own health checks, and a preflight check that fails stops the run with one message naming the
  broker that isn't there, instead of three hundred timeouts
  ([Containers](../parallel-ready-suites.md#step-4-containers),
  [Environment checks](../parallel-ready-suites.md#environment-checks-run-under-the-supervisor)).

### 12. Prefer technologies with a strong local story

::: tip
Emulator projects like [LocalStack](https://www.localstack.cloud/) or the latest version of the Azure Service Bus emulator are
changing this equation for the better and giving us more usable options -- but PostgreSQL and RabbitMQ are the undisputed champions
for local-friendly development in my humble opinion!
:::

Choose infrastructure a developer can run on a laptop *if you can*: Postgres and Marten in Docker, Fisher on a
file, RabbitMQ in a container. What you can run locally, you can reset, inspect and debug.

When a cloud-only service is unavoidable (Azure Service Bus, say), give each developer and each test
environment their own namespace, or a prefix on every resource name. The goal is predictable inputs:
a test can only expect an outcome if nobody else is feeding the same queue.

### 13. Collapse a distributed system into one process for testing

::: tip
For *functional* testing, yes. For any kind of load testing or testing for resiliency, you'll need
more black box testing, of course.
:::

Run the test harness, the web server and the database client in one process, so a test can start the
system, drive it and observe it without deployments, ports or timing between processes. Replace
dependencies that are hard to control (a third-party API, a payment gateway) with stubs, even in
functional tests. Register the stubs in the container.

Wolverine's in-process tracked sessions are the modern answer. A `WolverineSpec` act runs the
message, every handler it cascades to and every local queue it touches in the test's own process, and
waits for all of it to settle before the first `Then`
([Act](https://wolverinefx.net/guide/testing-with-bobcat#act)). BankAccountES's host is one `AlbaHost.For<Program>()` for
the whole run.

### 14. Continuous integration: fast tests and slow tests

::: info
This is old advice from the early days of Extreme Programming that's just meant to improve your
feedback cycles.
:::

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

### 15. Testability is an architectural concern

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
endpoint. That's why item 6's unit test exists, and why it needs nothing.

## Where to go next

- Writing the first specification -- [Specifications with Code](specifications-with-code.md)
- A slow or flaky suite -- [Reliable Integration Testing](reliable-integration-testing.md)
- The pipeline -- [Integrating Bobcat with CI](continuous-integration.md)
- Failures an agent can act on -- [Agent Friendly Integration Tests](agent-friendly-tests.md)

## More reading

The recommendations come from three older posts of Jeremy's:

- [My Recommendations for a Test Automation Strategy](https://jeremydmiller.com/2024/09/29/my-recommendations-for-a-test-automation-strategy/) (2024)
- [Succeeding with Automated Integration Tests](https://jeremydmiller.com/2015/06/25/succeeding_with_integration_testing/) (2015)
- [My Opinions on Data Setup for Functional Tests](https://jeremydmiller.com/2013/01/26/my-opinions-on-data-setup-for-functional-tests/) (2013)
