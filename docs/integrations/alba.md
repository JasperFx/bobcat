# Bobcat with Alba

`Bobcat.Alba` hosts your ASP.NET Core application in memory and gives your steps the HTTP calls
they make against it.

```csharp
[BobcatConfiguration]
public static void Configure(BobcatRunner runner)
    => runner.Resources.Add(new AlbaResource<Program>());
```

```csharp
[When("I create a student named {string}")]
public async Task CreateStudent(string name)
{
    var (status, student) = await Context!.PostJsonAsync<CreateStudent, Student>(
        "/api/students", new CreateStudent(name));

    _status = status;
    _id = student?.Id;
}
```

That is the whole integration. There is no separate wiring step: the resource is an
[`IHostResource`](../resources.md), so every `IStepContext` helper that resolves a host —
`Context.Host()`, `Context.GetService<T>()`, `Context.EventStore()` — finds it, and each scenario
gets its own DI scope.

## What it gives you

| | |
|---|---|
| `AlbaResource<TProgram>` | the application, started once for the suite and stopped at the end |
| `PostJsonAsync` / `PutJsonAsync` / `GetJsonAsync` / `DeleteAsync` | one call, returning `HttpResult<T>` — the status and the body if there was one |
| `SendAsync<T>(Action<Scenario>)` | the escape hatch: any Alba scenario, same result shape |

**The status is surfaced, never asserted.** Alba checks for a 200 unless told otherwise; a spec
asserts whatever status it expects, so a 404 a scenario is *testing for* must not fail the call
that produced it.

**Give it a reset hook if the application has persistent state**, and the suite will call it
between scenarios:

```csharp
runner.Resources.Add(new AlbaResource<Program>(reset: async host =>
{
    var store = host.Services.GetRequiredService<IDocumentStore>();
    await store.Advanced.Clean.DeleteAllDocumentsAsync();
    await store.Advanced.Clean.DeleteAllEventDataAsync();
}));
```

Pass `name:` when a suite drives more than one application, and the same name to any helper to
pick between them.

## Content roots

`AlbaResource` resolves the content root through [`AlbaContentRoot`](../resources.md) before
booting. This matters more than it sounds: `WebApplicationFactory` guesses
`<solution>/<assembly name>`, which is wrong for every project that does not sit directly under
the solution, and the failure arrives as a bare path in an exception message rather than an
explanation.

## Why this package is small

It was deleted, and rebuilt from what nine sample suites actually needed.

Each sample wrote its own Alba resource and its own HTTP helpers with no package to lean on. All
nine came out **byte-identical** — 100 lines apiece, 900 in total — and that file is this package.
What none of them reached for is deliberately absent: a factory-delegate form, `Restart`, console
log-level control, an explicit `WithContentRoot`, raw-response helpers for headers and undecoded
bytes, and the `IHttpResource` transport seam. `SendAsync` reaches the whole Alba `Scenario` API,
so anything the four shaped helpers do not cover is one line away — and anything genuinely missing
comes back when a spec needs it, not before.

One thing is here that no sample asked for. The hand-written copies implemented plain
`ITestResource`, which silently cost them every host-resolving helper in Bobcat and meant no
per-scenario DI scope was ever opened for them. The duplication hid a capability regression, so
the rebuilt resource is an `IHostResource`.

## See also

- [Resources](../resources.md) — the lifecycle every resource shares
- [Integrating Bobcat Gherkin](../integrating-gherkin.md) — `dotnet test` vs the command line runner
