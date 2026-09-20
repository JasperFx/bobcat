# Bobcat with Alba

`Bobcat.Alba` runs your ASP.NET Core application in-process and lets steps drive it over HTTP.
[Alba](https://jasperfx.github.io/alba) does the hosting and the request scaffolding; this package
makes it a Bobcat resource and puts the verbs on `IStepContext`.

```bash
dotnet add package Bobcat.Alba
```

## Register the host

```csharp
[BobcatConfiguration]
public static void Configure(BobcatRunner runner)
{
    runner.Suite.AddResource(new AlbaResource<Program>());
}
```

`AlbaResource` also takes a factory (`Func<IAlbaHost>` or `Func<Task<IAlbaHost>>`), an optional
`name`, and — importantly — a `reset` hook that runs between scenarios:

```csharp
new AlbaResource<Program>(reset: async host =>
{
    var store = host.Services.GetRequiredService<IDocumentStore>();
    await store.Advanced.Clean.DeleteAllDocumentsAsync();
});
```

The resource exposes `AlbaHost`, `Host`, `RootServices` and `CurrentServices`, and implements
`IRestartableResource` so a suite can restart the host mid-run when it needs to.

## Drive it from steps

```csharp
[Given("a customer named {string}")]
public async Task Customer(string name)
{
    var result = await Context!.PostJsonAsync<CreateCustomer, Customer>(
        "/customers", new CreateCustomer(name));
}
```

| Method | Shape |
|---|---|
| `PostJsonAsync<TRequest, TResponse>(url, body)` | → `HttpResult<TResponse>` |
| `PutJsonAsync<TRequest, TResponse>(url, body)` | → `HttpResult<TResponse>` |
| `GetJsonAsync<TResponse>(url)` | → `HttpResult<TResponse>` |
| `DeleteAsync(url)` | → `HttpResult<object>` |

Each takes an optional trailing `resourceName` for suites running more than one host.

Three things about these signatures that cost people time:

- **`Context` is `IStepContext?`**, so `Context!` is load-bearing in a nullable-enabled project.
- **Both type arguments are required on `PostJsonAsync`/`PutJsonAsync`.** `TResponse` cannot be
  inferred from the call, and C# infers all-or-nothing — omitting them is `CS0411`, not a helpful
  default. Use `object` when you genuinely do not care about the body.
- **Alba's implicit 200 assertion is suppressed.** These helpers call `IgnoreStatusCode()` and
  surface the status on `HttpResult` instead, so a scenario that deliberately exercises a 404 or a
  201 is not failed for you. Assert the status yourself.

## Reaching past the helpers — raw responses

You rarely need to touch Alba directly. When a step is about the *representation* rather than a
deserialized body — an export's JUnit XML or NDJSON, a download, a problem-details payload — three
helpers hand you the response as it came:

| | |
|---|---|
| `GetRawAsync(url)` | → `RawResponse`: status, `ContentType`, `MediaType`, headers, `Body`/`Bytes`, `ReadAsJson<T>()` |
| `PostRawAsync(url, body, contentType)` | sends a raw body |
| `SendRawAsync(s => …)` | runs any Alba scenario with the status surfaced rather than asserted |

A sample that reaches into `host.Scenario(...)` itself will trip over Alba's default 200 assertion
on any non-200 path (201, 204, 404). These helpers and the JSON ones all call `IgnoreStatusCode()`
for you.

## `HttpResult.Body` is non-null on a 400 — and it is not what you asked for

This is the one that costs the most time, because it fails far from its cause.

The helpers deserialize whatever came back into `TResponse` and swallow the failure.
System.Text.Json ignores unknown properties, so a `ProblemDetails` body reads into your `Customer`
or `TodoList` without complaint — every property at its default. If the response type initialises
an id:

```csharp
public Guid Id { get; set; } = Guid.NewGuid();
```

…then your fixture now holds a perfectly plausible id for a resource that was never created, and
the next step 404s somewhere far from the 400 that caused it.

**Gate on the status before taking anything from the body:**

```csharp
if (result.StatusCode is >= 200 and < 300 && result.Body is not null)
{
    _id = result.Body.Id;
}
```

A `Given` that does not assert its own status is exactly where this hides, because nothing reports
the 400.

## Content root

`WebApplicationFactory` guesses the host's content root as `<solution dir>/<assembly name>`,
unchecked — which is wrong for a nested `Tests/` layout (it doubles the path) and for any web
project under `src/`. When the host builds, it surfaces as a bare `DirectoryNotFoundException`.

**`AlbaResource<TProgram>` does not leave this to the factory.** `AlbaContentRoot.Resolve` mirrors
the factory's order and fixes its two habits: the manifest is read from the test **output**
directory wherever the process was started from, and the solution-relative guess is *checked*,
falling back to searching for `<assembly name>.csproj` below the solution and finally to the test
output directory itself — which always exists, and into which the build has already copied the
host's `appsettings*.json`.

Sibling, `src/`, and nested-`Tests/` layouts all work with no attribute. `resource.ContentRoot`
tells you what was decided and why.

Still yours to set when the host wants a directory none of those are:

```csharp
new AlbaResource<Program>().WithContentRoot(path);
```

::: warning The factory-delegate form resolves nothing
`new AlbaResource(async () => await AlbaHost.For<Program>(…))` builds the host from your lambda, so
Bobcat never sees a `TProgram` and cannot resolve anything on your behalf — the factory's own
guessing applies in full, including the doubled path. Either pin it in the factory with
`x.UseContentRoot(appDirectory)`, or use the typed `AlbaResource<Program>`, which resolves it
itself.
:::

## Other things worth knowing

- **A durable local queue makes the HTTP response an unreliable moment to assert** — the response
  returns before the cascade finishes. Wait for the messages; see
  [Bobcat with Wolverine](wolverine.md).
- **Host console logging floods the test output.** `AlbaResource<T>` floors the hosted
  application's console logging at `Warning` — see [Resources](../resources.md#quieting-a-noisy-host).
- **Seed data in `Program.cs` runs under a test host**, and your reset hook is what removes it —
  see [Resources](../resources.md#resetting-between-scenarios).
