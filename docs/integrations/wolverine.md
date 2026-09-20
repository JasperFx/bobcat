# Bobcat with Wolverine

`Bobcat.Wolverine` gives steps access to [Wolverine](https://wolverinefx.net)'s tracked sessions —
so a step can cause a message and then wait for *everything that message caused* to settle before
asserting. It also handles two things that otherwise make a Wolverine suite slow or flaky: handler
warm-up and transport draining.

```bash
dotnet add package Bobcat.Wolverine
```

It needs an `IHostResource` in the suite, which `AlbaResource` already is — see
[Bobcat with Alba](alba.md).

## The core problem this solves

A Wolverine handler rarely does one thing. It cascades: a command produces events, events produce
more messages, some of them travel through a local queue. **The HTTP response comes back before any
of that finishes**, so asserting at that moment is asserting on a system still mid-flight. The
result is a suite that passes on a fast machine and fails on a loaded one.

A tracked session is the fix: cause the work, wait for the whole cascade, then assert.

## Causing work and waiting for it

```csharp
[When("the order is placed")]
public async Task PlaceOrder()
{
    _session = await Context!.InvokeMessageAndWaitAsync(new PlaceOrder(_orderId));
}
```

| Method | Use it when |
|---|---|
| `InvokeMessageAndWaitAsync(message)` | Invoke a message locally, wait for every cascade |
| `InvokeMessageAndWaitAsync<T>(message)` | Same, and you want the return value — hands back `(ITrackedSession, T?)` |
| `SendMessageAndWaitAsync<T>(message)` | Send rather than invoke, wait for the cascade |
| `ExecuteAndWaitAsync(action)` | **Wrap anything at all** — an Alba HTTP call, a client call — in a tracked session |
| `TrackActivity()` | The raw `TrackedSessionConfiguration`, for manual coordination |

All take an optional `resourceName` and a `timeoutInMilliseconds` that defaults to 5000.

`ExecuteAndWaitAsync` is the one to reach for with an HTTP-driven suite, because it brackets a call
made from the outside:

```csharp
_session = await Context!.ExecuteAndWaitAsync(() =>
    Context.PostJsonAsync<PlaceOrder, OrderResponse>("/orders", new PlaceOrder(id)));
```

The package needs no HTTP dependency to do this — the call is supplied as a delegate.

## The session is the diagnostic, not just the barrier

This is the part most suites leave on the table. `ITrackedSession` holds the entire messaging
story, and a test that only asserts an outcome throws all of it away:

- **`AllRecordsInOrder()`** — the causal chain, in order. This is what you cannot reconstruct from
  a failed assertion.
- **`AllExceptions()`** — everything that threw anywhere in the cascade, including in a handler
  that a green HTTP response said nothing about.
- **`Status`** — how the session ended, including whether it timed out rather than completed.

Surfacing that material in a failure is the subject of
[Agent Friendly Integration Tests](../tutorials/agent-friendly-tests.md).

::: warning Do not read a green run as proof the race is absent
A suite with the same shape passed **10 of 10** runs with the tracking removed, because a
one-document handler usually beats the follow-up GET. Usually. Keep the tracking wherever the
cascade exists, and record the measurement either way so the next reader knows which kind of suite
they have.
:::

## Handler warm-up

Wolverine compiles a handler chain on first use. Inside a tracked session's timeout window, that
compilation is charged against the timeout — so the first scenario of a run can fail for being
first rather than for being wrong.

Every helper above warms the host before opening its session, so you get this by default.
`HandlerWarmUp.Automatic` controls it process-wide (default `PrimeCompiler`), and
`host.WarmUpHandlers()` compiles every chain the host knows about, once per host.

> **HTTP endpoints are not covered.** A Wolverine.HTTP route compiles on its first request, and
> this package has no Wolverine.HTTP reference to reach it. Wolverine owns that switch:
> `app.MapWolverineEndpoints(opts => opts.WarmUpRoutes = RouteWarmup.Eager)`.

## Transport draining

```csharp
var drained = await host.DrainTransportsAsync();
```

A per-scenario reset clears the **store** but not the **broker**. An outbox row is deleted while
the queued message is still sitting there, ready to be delivered into the next scenario and fail an
assertion that has nothing to do with it. Draining is how you close that gap; it returns the number
of messages it cleared.

## Where to go next

- [The Run Lifecycle](../run-lifecycle.md) — where tracking, warm-up and draining sit in the schedule
- Marten alongside Wolverine — [Bobcat with Marten](marten.md)
