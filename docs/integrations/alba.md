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

## The footguns worth knowing before you start

Wiring a real host has a playbook with eighteen of these, found by doing it. The ones that bite
first:

- **`HttpResult.Body` is non-null on a 400**, and it is not the thing you asked for.
- **A durable local queue makes the HTTP response an unreliable moment to assert** — the response
  returns before the cascade finishes. Wait for the messages; see
  [Bobcat with Wolverine](wolverine.md).
- **Content root** under a nested `Tests/` directory needs
  `[assembly: WebApplicationFactoryContentRoot(...)]`.
- **Host console logging floods the test output** — `AlbaResource<T>` floors it at Warning for you.
- **Seed data in `Program.cs` runs under Alba**, and your reset hook is what removes it.

All eighteen, with the symptom each one presents as: [Wiring a Real Host](../wiring-a-real-host.md).
