# Diagnostics

Bobcat checks your specifications while they compile. The source generator in `Bobcat.Generators`
reports `BOBCATnnn` diagnostics to the compiler, so they appear in the build output, in the IDE's error
list and as squiggles under the code they are about. One more, BOBCAT033, is an MSBuild warning from
the same package. They catch the mistakes that would otherwise show up only as a spec that silently
records nothing, a stale table column, or a hole in the Event Model.

An **error** stops the build. A **warning** and an **info** don't. Turn a warning into an error in CI
the usual way:

```xml
<WarningsAsErrors>$(WarningsAsErrors);BOBCAT027;BOBCAT028</WarningsAsErrors>
```

| Id | Severity | Meaning |
|---|---|---|
| [BOBCAT023](#bobcat023) | error | `SliceName` and `SliceType` name different slices |
| [BOBCAT024](#bobcat024) | warning | A literal `SliceName` names a type: use `SliceType` |
| [BOBCAT027](#bobcat027) | warning | A step template placeholder names no parameter |
| [BOBCAT028](#bobcat028) | warning | A class binds a slice but nothing opens a recording |
| [BOBCAT029](#bobcat029) | info | An `And`/`But` comment was read as prose, not a step |
| [BOBCAT032](#bobcat032) | error | A table column or member path names no member of its type |
| [BOBCAT033](#bobcat033) | warning (MSBuild) | An xUnit v3 project has no `Microsoft.NET.Test.Sdk` |
| [BOBCAT034](#bobcat034) | error | An Event Model role or trigger names no parameter of its method |
| [BOBCAT036](#bobcat036) | error | Two specifications share one spec identity |
| [BOBCAT037](#bobcat037) | error | Bobcat's interceptors aren't enabled in the project |
| [BOBCAT038](#bobcat038) | info | An HTTP act's route can't be known at compile time |
| [BOBCAT039](#bobcat039) | error | A step helper isn't accessible to the interceptor |
| [BOBCAT040](#bobcat040) | error | An override of an inherited `[BobcatSpec]` carries another test attribute |
| [BOBCAT041](#bobcat041) | info | A specification reaches no Event Model slice |
| [BOBCAT042](#bobcat042) | error | A `[BobcatExamples]` table doesn't fit its theory |
| [BOBCAT043](#bobcat043) | warning | A Task-returning step is called and never awaited |
| [BOBCAT044](#bobcat044) | error | A member check is not one assertion on one member |
| [BOBCAT045](#bobcat045) | info | A specification's act goes through a helper that declares no Event Model role |

Ids that are not listed were [retired](#retired).

## BOBCAT023 — `SliceName` and `SliceType` name different slices {#bobcat023}

**Error.** `SliceName = "X" and SliceType = typeof(Y) name different slices. SliceType means exactly
SliceName = type.Name — set one of them.`

**When:** a `[BobcatSlice]` or `[BobcatSpec]` sets both spellings of the slice and they disagree. This
holds across the two attributes too: a method carrying both is one binding stated twice.

**Why:** one of the two is wrong, and picking a silent winner would make the other slice vanish
without a word. Setting both to the *same* slice is redundant but allowed.

**Fix:** keep one. Prefer `SliceType`:

```csharp
[BobcatSpec(typeof(ConfirmAppointment))]   // not SliceName = "ConfirmAppointment" as well
public async Task confirming_an_open_slot() { /* … */ }
```

See [Two spellings, and why `SliceType` is preferred](marker-steps.md#two-spellings-and-why-slicetype-is-preferred).

## BOBCAT024 — prefer `SliceType` over a literal `SliceName` {#bobcat024}

**Warning.** `[BobcatSpec(SliceName = "X")] names a slice that IS a type in this compilation. Prefer
SliceType = typeof(X): …`

**When:** `SliceName` is a string literal and a type with exactly that name exists in the compilation.

**Why:** a type is rename-safe and survives to the generator, where a string can't be checked against
the model.

**Fix:** `[BobcatSpec(typeof(X))]` or `SliceType = typeof(X)`. Where no type bears the slice's name
(an Automation slice usually has none) there is no warning, and `SliceName` is the right spelling.

## BOBCAT027 — step template names no parameter {#bobcat027}

**Warning.** `Step attribute on 'M' has the placeholder '{p}', but the method has no parameter named
'p' (it takes a, b). Nothing can fill it, so the step renders as '{p}'.`

**When:** a `[Given]`/`[When]`/`[Then]`/`[Check]`/`[Step]`/`[BobcatStep]` expression contains a
`{placeholder}` that matches no parameter of the method. Reported once per helper and placeholder,
not once per call.

**Why:** a placeholder binds **by parameter name only**. Cucumber-style type captures (`{int}`,
`{word}`) left with Gherkin and land here too.

**Fix:** rename the placeholder (or the parameter) so they match:

```csharp
[When("the customer orders {quantity} widgets")]
public void Order(int quantity) { /* … */ }
```

See [A placeholder binds from the value, not from the syntax](marker-steps.md#a-placeholder-binds-from-the-value-not-from-the-syntax-issue-339).

## BOBCAT028 — a slice is bound but nothing records {#bobcat028}

**Warning.** `'C' binds a slice with [BobcatSlice] but nothing opens a scenario, so none of its steps
are recorded and it reaches the Event Model as no specification at all. …`

**When:** a `[BobcatFeature]` class binds a slice (on the class or a method) but nothing opens a
recording: no `[BobcatScenario]` on the class, no `[BobcatSpec]`/`[BobcatTheory]`/`[BobcatScenario]`
on its methods, and no `[assembly: Bobcat.Xunit.RecordBobcatFeatures]` hook (the `Bobcat.Xunit`
package applies that for you).

**Why:** without an open recording every step records into nothing and the suite passes having
produced no specification. Nothing at run time can tell that apart from a suite with nothing to
record, so the compiler is the only place to catch it.

**Fix:** add the runner's `[BobcatScenario]` to the class (`Bobcat.Xunit`, `Bobcat.TUnit`,
`Bobcat.NUnit` or `Bobcat.MSTest`), or on xUnit replace `[Fact]` + `[BobcatSlice]` with one
`[BobcatSpec(typeof(TheSlice))]` per test, which can't come apart. See
[`[BobcatSpec]`](marker-steps.md#bobcatspec).

## BOBCAT029 — a comment read as prose, not a step {#bobcat029}

**Info.** `The comment "And …" opens with 'And', but no step has opened the narrative in this test, so
it is read as an ordinary comment rather than a step. …`

**When:** a comment in a test body starts with `And` or `But` before any `Given`/`When`/`Then`
comment has opened the narrative.

**Why:** English sentences start with "And" all the time, and most comments aren't steps. A keyword
that can only *continue* a narrative isn't allowed to start one. Info, because usually the comment
really is prose.

**Fix:** if you meant a step, open with `Given`, `When` or `Then`, or mark it as a keywordless step:

```csharp
// * the ledger is reconciled
```

Inside a test that is already narrating, `And …` is taken as a step. See
[Marker comments](marker-steps.md#marker-comments).

## BOBCAT032 — table column names no member {#bobcat032}

**Error.** For example: `The table column 'Town' does not match Address: no 'Town' on Address — it
has City, Line1, Zip. Check the spelling, or the member may have been renamed since this spec was
written`. Variants name a `path` or a `key column`, or a `[BobcatExamples]` column that "names no
parameter".

**When:** a **constant** table (a literal, a raw string, or a constant interpolated string such as
`$"| {nameof(Order.Total)} |"`) or member path names something the type doesn't have. Checked for:

| Call | Type checked |
|---|---|
| `PropertyCells.Verify(subject, table)` and any `(object, StepTable)` pair | the subject's static type; dotted paths followed |
| `SetVerificationComparer.Verify(actual, table, keyColumns: …)` and any `(IEnumerable, StepTable)` pair | the element type, top-level columns only; constant key columns too |
| `PartialObjects.FromTable(typeof(T), table)` | `T` — members or constructor parameters, dotted |
| `Specify<T>().With("path", value)` | `T` |
| `Expect.Rows(table)` / `Expect.NoRowsLike(table)` inside a `With(…)` | the member's element type |
| `[BobcatExamples("…")]` | the theory's parameters |

Columns match by name or by `[Header]` title (a title *replaces* the name). A path that indexes into a
collection (`Items[0].Sku`), a dotted column in a set verification, or a path deeper than 8 levels is
refused with its own message.

**Why:** a stale column is always a broken spec. Catching it in the build beats finding it on the next
run.

**Fix:** correct the column, or write it with `nameof` so a rename keeps it in step.

**When it doesn't fire:** a table built at run time, a subject typed `object`, an interface, an
abstract type or a type parameter, and — for a property check or a set — an open class that something
derives from (a subclass may carry the column). Those are checked **at run time** instead: the column
becomes an `invalid` cell that lists what the type does have. See
[One object against one row](tutorials/data-intensive-specifications.md#one-object-against-one-row)
and [Expected Objects](expected-objects.md).

## BOBCAT033 — no `Microsoft.NET.Test.Sdk` in an xUnit v3 project {#bobcat033}

**Warning (MSBuild).** `This xUnit v3 project references no Microsoft.NET.Test.Sdk, so Rider and Visual
Studio will find no tests in it (the command line still runs them). …`

**When:** an executable project references `xunit.v3` (or `xunit.v3.core`) but not
`Microsoft.NET.Test.Sdk`. TUnit projects are not checked.

**Why:** the IDEs discover tests through the VSTest bridge that `Microsoft.NET.Test.Sdk` and
`xunit.runner.visualstudio` provide. Without them the suite runs from `dotnet test` and shows nothing
in the test explorer.

**Fix:** add both package references, or set `<BobcatSuppressTestSdkWarning>true</BobcatSuppressTestSdkWarning>`
if the suite only ever runs from the command line. See [Getting Started](getting-started.md)
and [IDE integration](tutorials/ide-integration.md#specifications-in-the-test-explorer).

## BOBCAT034 — an Event Model role names no parameter {#bobcat034}

**Error.** `From = "cmd" on 'WhenReceived' names no parameter or type parameter of it, so the role is
never stamped at any call. It has: command, T. Write it with nameof(…) so a rename keeps the two in step.`

**When:** an `[EventModelRole]`'s `From`, or an `[EventModelTrigger]`'s `Route` or `RouteName`, names
no parameter of the vocabulary method it decorates (`From` may also name a type parameter). Reported
where the vocabulary is compiled.

**Why:** at a call site in another assembly the same mistake would just be a role that is never
stamped on the slice, with nothing to say so.

**Fix:** name a real parameter, with `nameof`:

```csharp
[EventModelRole(SliceRole.Command, nameof(command))]
[EventModelTrigger(ActTrigger.Http, Route = nameof(route))]
public Task WhenPosted<T>(string route, T command) { /* … */ }
```

See [Binding, not roles](marker-steps.md#event-model-roles).

## BOBCAT036 — two specifications share one spec identity {#bobcat036}

**Error.** `The spec identity 'Orders/order is placed' is also claimed by …. Identity filtering, the
resident runner, the flake/duration ledger and the Event Model would treat them as one specification. …`

**When:** two projected tests in one compilation resolve to the same `{Feature}/{Scenario}` identity:
same-named methods in two classes bound to one feature, two method names that read as the same sentence
(`order_is_placed` and `order__is_placed`), or two data rows of one method that format alike or share a
label (`1` and `01`). Reported at each claimant, naming the others. Compared exactly, case included.

**Why:** everything keyed on identity would see one spec: running by identity, the resident runner, the
ledger, and the Event Model's spec list.

**Fix:** rename one method, give one an explicit name with `[BobcatSpec(Name = "…")]`, or for rows change
or remove the repeated row, or label it with `[BobcatExample("label", …)]`. Duplicates the compiler can't
see together (two assemblies) are reported when the spec manifest is written, and running by such an
identity is refused. See [Two specs, one identity](spec-identities.md#two-specs-one-identity) and
[An explicit scenario name](marker-steps.md#explicit-name).

## BOBCAT037 — interceptors aren't enabled {#bobcat037}

**Error.** `This project has N Bobcat step calls to intercept, but interceptors are not enabled for the
Bobcat.Generated namespace, so no step would record and a SpecAssert inside a step helper would report
to nothing. …`

**When:** the project calls step helpers (or has projected assertions) but `Bobcat.Generated` isn't in
`InterceptorsNamespaces`. Reported once, at the first call site.

**Why:** the `Bobcat.Generators` package adds the opt-in from its `buildTransitive` props. A
`ProjectReference` to the generator takes no build assets, so it gets the generator without the opt-in.
Without it the helpers would run as plain method calls and a failing `SpecAssert` would land nowhere.

**Fix:**

```xml
<InterceptorsNamespaces>$(InterceptorsNamespaces);Bobcat.Generated</InterceptorsNamespaces>
```

or import the generator's `buildTransitive/Bobcat.Generators.props`. See
[Referencing Bobcat by ProjectReference](getting-started.md#referencing-bobcat-by-projectreference).

## BOBCAT038 — an HTTP act's route can't be known {#bobcat038}

**Info.** `'WhenPosted' is this specification's HTTP act, but its 'route' argument is neither a constant
nor an interpolated string, so the route is only known at run time. The slice keeps its trigger kind with
no route. …`

**When:** the spec's HTTP act (the last act in the test) passes a route the generator can't resolve: a
helper call or a variable, a route name no endpoint attribute declares (a minimal API's `.WithName`), a
route name on several endpoints with different templates, or no route at all. An act that takes only the
command (`WhenPosted(cmd)`) gets it when no endpoint attribute takes that command as its body (a minimal
API's `MapPost`), several do with different templates, or none of the act's verb does.

**Why:** info, because the slice is still right; only the route is missing from the canvas.

**Fix:** pass the endpoint's route template as a constant (`"/api/accounts/{accountId}/deposits"`, values
after it), an interpolated string, or a route name an endpoint attribute declares. Or leave it. See
[The act's route](marker-steps.md#act-route).

## BOBCAT039 — step helper isn't accessible to the interceptor {#bobcat039}

**Error.** `Step helper 'M' is protected, so Bobcat's generated interceptor cannot call it and its calls
would record no step. Make it public or internal.`

**When:** a step helper is `protected`, `private`, `private protected`, or declared on a type the rest of
the assembly can't reach (a private nested class). Reported once, at the helper.

**Why:** the interceptor is generated code outside the class, so it can only call what the assembly can
see. Left unintercepted, the helper would open no step.

**Fix:** make the helper `public` or `internal` (`protected internal` works too). Marker comments have no
such constraint. See [A decorated helper cannot be `protected`](marker-steps.md#a-decorated-helper-cannot-be-protected).

## BOBCAT040 — an override of an inherited `[BobcatSpec]` carries another test attribute {#bobcat040}

**Error.** `This override inherits [BobcatSpec] from the method it overrides, so with this test attribute
too xUnit sees two Fact-derived attributes and fails the test at run time …`

**When:** an `override` of a test that inherits `[BobcatSpec]` or `[BobcatTheory]` carries a different
test attribute, usually `[Fact(Skip = "…")]` to skip one subclass's copy.

**Why:** xUnit inherits the base method's attribute, so the override has two and fails ("has multiple
[Fact]-derived attributes") instead of being skipped.

**Fix:** stay in the family:

```csharp
[BobcatSpec(Skip = "not supported by this transport")]
public override Task delivers_in_order() => base.delivers_in_order();
```

See [A test inherited from a shared base](spec-identities.md#inherited-specs).

## BOBCAT041 — a specification reaches no Event Model slice {#bobcat041}

**Info.** `The specification 'Orders/X' is on no Event Model slice: it names no slice and calls no
role-stamped vocabulary, so nothing says which slice it describes. Bind it to the slice it describes with
[BobcatSpec(typeof(TheCommandOrReadModel))] or [BobcatSpec(SliceName = "...")].`

**When:** only in a suite whose generated Event Model has at least one slice, for a projected spec that:

- is a `[BobcatSpec]` with no slice binding and no role-stamped vocabulary,
- states only document types, which no slice carries, or
- is unbound, and its feature's other unbound specs make different acts, while its own act is a query or
  names no command, so it can't be placed without a guess.

**Why:** info, because the test runs and reports as before; only the model is missing it.

**Fix:** bind it: `[BobcatSpec(typeof(PlaceOrder))]` or `[BobcatSpec(SliceName = "…")]`. A spec on no
slice also has no coordinates for `bobcat run --slice`/`--chapter`/`--domain`/`--pattern`. See
[An act that names no command](marker-steps.md#unstamped-act).

## BOBCAT045 — an act goes through a helper that declares no Event Model role {#bobcat045}

**Info.** `'WhenPosted' declares no Event Model role, but it calls 'WhenActing', which is an act, so this call is
read as the test's act and its command is taken from the slice binding, AddBook, because the call passes one. Its
trigger (an HTTP verb and route) is not read through it. Declare [EventModelRole(SliceRole.Command,
nameof(command))] on 'WhenPosted' …`

**When:** a projected spec's last act is a method of the project's own -- usually an HTTP helper on a spec base
class, `WhenPosted(object command, string url)` -- that carries no `[EventModelRole]`, `[EventModelTrigger]` or
`[EventModelAct]`, but whose body calls one that does, and the command it sends is passed in rather than named in
its body. The second half of the message says either that the command came from `[BobcatSpec(typeof(X))]`
(the call passes an `X`), or that which command it sends can't be known, so the slice has no command type.

**Why:** info, because the slice is already right where it can be: the call is the act, so the slice is never a
View, and it is a Command slice when the binding names the command. Before bobcat#681 the act was silently
lost and such a spec made a View slice. What can't be recovered is the trigger: the inner act's route is the
helper's parameter.

**Fix:** put the attributes on the helper, naming its own parameters:

```csharp
[EventModelRole(SliceRole.Command, nameof(command))]
[EventModelTrigger(ActTrigger.Http, Route = nameof(url), HttpMethod = "POST")]
protected Task WhenPosted(object command, string url) => WhenActing(…);
```

It is *not* reported for a helper whose own body names its command (`OpenTheAccount() =>
WhenReceived(new OpenAccount(…))`): that command and its trigger are read through it. See
[An act that names no command](marker-steps.md#unstamped-act), and the paragraph before it on helpers.

## BOBCAT042 — an inline examples table doesn't fit its theory {#bobcat042}

**Error.** For example: `Row 2 of [BobcatExamples] on 'discount_is_applied': the cell 'ten' in column
'amount' cannot be read as decimal: …`

**When:** a `[BobcatExamples]` table the runtime would refuse:

- no header row,
- a parameter with no column and no default value,
- a column given twice,
- a header and no rows,
- a row with the wrong number of cells,
- a cell its parameter's type can't read: a number that doesn't parse, an enum value no member names,
  `NULL` or `EMPTY` for a non-nullable value type.

A column that names no parameter is [BOBCAT032](#bobcat032).

**Why:** every one of these would fail the theory before any row runs.

**Fix:** correct the table:

```csharp
[BobcatTheory(typeof(ApplyDiscount))]
[BobcatExamples("""
    | amount | tier | expected |
    | 100    | Gold | 90       |
    """)]
public async Task discount_is_applied(decimal amount, Tier tier, decimal expected) { /* … */ }
```

A cell for a domain type registered with `CellValues.Register<T>` is left to run time. See
[A theory's examples as a table](marker-steps.md#bobcatexamples).

## BOBCAT043 — a Task-returning step is not awaited {#bobcat043}

**Warning.** `'ThenDocument' returns Task and is not awaited, so its step runs unobserved: it may finish
after the scenario has closed, and a failure in it never fails the test. Add 'await' (and make the test
async).`

**When:** a step helper or Event Model vocabulary method (`[Given]`…`[Step]`, `[BobcatStep]`,
`[EventModelRole]`, `[EventModelTrigger]`, `[EventModelAct]`) that returns a `Task` or `ValueTask` is
called as a statement, or discarded with `_ =`, and never awaited: `ThenDocument<Order>(id);` where
`await ThenDocument<Order>(id);` was meant. A Task that is returned, stored, or passed to
`Task.WhenAll` is not reported.

**Why:** the spec passes whatever the step finds. The compiler says only CS4014, and only inside an
`async` method. It is *not* fixed by awaiting every call: some scenario steps are synchronous
(`ThenEvents`), and `await` on one of those is CS4008.

**Fix:** add `await` to the call that was reported, and make the test `async` if it isn't.

## BOBCAT044 — a member check is not one assertion on one member {#bobcat044}

**Error.** For example: `asserts on 'x.Attendees.ShouldHaveSingleItem().MemberId', which is not a member
of the object. A member check is one assertion on one property or field path, …`

**When:** a member check (`Specify<T>(x => …)`, `.Check(x => …)`) that is not one assertion on one member
path:

- chained Shouldly, such as `x => x.Attendees.ShouldHaveSingleItem().MemberId.ShouldBe(id)`,
- an assertion on the object itself (`x => x.ShouldNotBeNull()`),
- one comparing two members of the same object (`x => x.Total.ShouldBe(x.Subtotal)`).

**Why:** Bobcat reads a check as a member path and a value. Without the generator, the run refuses each
of these with an `ArgumentException` that ends the test.

**Fix:** assert on one member, and specify a collection member's items instead of chaining into them:

```csharp
Specify<Meeting>().With(x => x.Attendees, Expect.Exactly(Specify<Attendee>().With(a => a.MemberId, theMemberId)));
```

See [member checks](expected-objects.md#member-checks-and-the-c-version-they-need).

## Retired

These ids are no longer reported by anything. **An id is never reused**, so a suppression or a search for
one keeps meaning one thing.

| Ids | Retired with |
|---|---|
| BOBCAT001–022, BOBCAT030, BOBCAT031, BOBCAT035 | the Gherkin lane, removed in 0.50.0 (bobcat#471) |
| BOBCAT025, BOBCAT026 | `*.spec-ownership.yaml` and the curated Event Model format (bobcat#406) |

A `<NoWarn>` or `#pragma` for a retired id is harmless and can be deleted.
