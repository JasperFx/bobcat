using Bobcat.Engine;
using Microsoft.Extensions.Hosting;

namespace Bobcat.Runtime;

/// <summary>
/// Everything the run owns a lifecycle for: registration, start-up, per-scenario reset, and
/// teardown. Registered things start in registration order and stop in reverse.
/// </summary>
/// <remarks>
/// <para>
/// This holds <see cref="IHostedService"/>, not a Bobcat-only interface. An
/// <see cref="ITestResource"/> is one — it adds a name, a between-scenario reset and a preflight
/// check — but a plain hosted service is registrable too, and that is what replaced the old
/// <c>IGlobalAction</c>: cross-cutting set-up that runs once for the whole run is a
/// <c>StartAsync</c>/<c>StopAsync</c> pair like everything else, so there is one list, one
/// ordering rule, and one interface to learn.
/// </para>
/// <para>
/// <strong>Registration order is the only ordering lever</strong>, and it now spans both kinds.
/// A seeding service registered after the database resource it writes to starts after it and
/// stops before it. Under <c>IGlobalAction</c> every resource started before every global action;
/// now a global action registered first genuinely runs first, which is worth knowing when moving
/// one across.
/// </para>
/// </remarks>
public class TestResources : IAsyncDisposable
{
    private readonly List<IHostedService> _services = new();
    private readonly List<ITestResource> _resources = new();
    private readonly Dictionary<string, ITestResource> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IHostedService> _attempted = new();

    /// <summary>
    /// Register something with a lifecycle. An <see cref="ITestResource"/> is additionally indexed
    /// by its <see cref="ITestResource.Name"/> so a step can look it up.
    /// </summary>
    public TestResources Add(IHostedService service)
    {
        if (service is ITestResource resource) return Add(resource.Name, resource);

        _services.Add(service);
        return this;
    }

    /// <summary>
    /// Register a resource under a name of your choosing, rather than the one it reports. The
    /// name is how a step reaches it when several of the same type are registered.
    /// </summary>
    public TestResources Add(string name, ITestResource resource)
    {
        if (_byName.ContainsKey(name))
            throw new ArgumentException($"A resource named '{name}' is already registered.");

        _services.Add(resource);
        _resources.Add(resource);
        _byName[name] = resource;
        return this;
    }

    /// <summary>
    /// Start everything in registration order. Any failure is catastrophic: the exception wraps in
    /// a <see cref="SpecCatastrophicException"/> naming what failed, and nothing after it is asked
    /// to start. The ones before it are up, and the one that threw may be half up —
    /// <see cref="DisposeAsync"/> tears both down.
    /// </summary>
    public async Task StartAll(CancellationToken token = default)
    {
        foreach (var service in _services)
        {
            // Recorded before StartAsync so something that throws part-way through — containers
            // up, health check failed — still gets its teardown.
            _attempted.Add(service);

            try
            {
                await service.StartAsync(token);
            }
            catch (Exception ex)
            {
                // A bind collision names the process holding the port (issue #200): without it,
                // "failed to start" over somebody else's orphan reads as a product regression,
                // and the diagnosis costs minutes that one lsof call would have cost nobody.
                throw new SpecCatastrophicException(
                    $"{describe(service)} failed to start: {ex.Message}{PortHolder.Explain(ex)}", ex);
            }
        }
    }

    /// <summary>
    /// Reset every resource between scenarios. A plain hosted service has no notion of a scenario
    /// and is left alone.
    /// </summary>
    public async Task ResetAll()
    {
        foreach (var resource in _resources)
        {
            await resource.ResetBetweenScenarios();
        }
    }

    /// <summary>
    /// Open a per-scenario DI scope on every host resource, in registration order.
    /// Called by the runner AFTER <see cref="ResetAll"/> — persistent state is cleaned
    /// first, then a fresh scope is opened over it.
    /// </summary>
    public async Task BeginScenarioAll()
    {
        foreach (var resource in _resources.OfType<IHostResource>())
        {
            await resource.BeginScenarioScope();
        }
    }

    /// <summary>
    /// Dispose each host resource's per-scenario DI scope, in reverse registration order.
    /// </summary>
    public async Task EndScenarioAll()
    {
        var hosts = _resources.OfType<IHostResource>().ToList();
        for (var i = hosts.Count - 1; i >= 0; i--)
        {
            await hosts[i].EndScenarioScope();
        }
    }

    /// <summary>
    /// Look up a resource by type and optional name.
    /// If name is null and exactly one resource of that type exists, returns it.
    /// If multiple exist, throws — caller must provide a name.
    /// </summary>
    public T GetResource<T>(string? name = null) where T : class, ITestResource
    {
        if (name != null)
        {
            if (_byName.TryGetValue(name, out var resource) && resource is T typed)
                return typed;

            throw new InvalidOperationException(
                $"No resource named '{name}' of type {typeof(T).Name} found.");
        }

        var matches = _resources.OfType<T>().ToList();
        return matches.Count switch
        {
            0 => throw new InvalidOperationException(
                $"No resource of type {typeof(T).Name} registered."),
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Multiple resources of type {typeof(T).Name} registered. Specify a name.")
        };
    }

    /// <summary>The named, resettable, preflight-checkable subset — what <see cref="Preflight"/> reads.</summary>
    public IReadOnlyList<ITestResource> Resources => _resources;

    /// <summary>Everything registered, resources and plain hosted services alike, in registration order.</summary>
    public IReadOnlyList<IHostedService> Services => _services;

    /// <summary>
    /// Tear down everything that <see cref="StartAll"/> started or tried to start, in reverse
    /// registration order. Teardown is <c>StopAsync</c>, reached through <c>DisposeAsync</c> for
    /// an <see cref="ITestResource"/> — see <see cref="ITestResource"/> for why that indirection
    /// exists. Something never asked to start is not touched: its teardown was written assuming
    /// <c>StartAsync</c> ran, and a second exception from tearing down something that never came
    /// up would only bury the one that matters. Everything gets its turn even if an earlier one
    /// threw; the failures surface together once they have all run.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<Exception>? failures = null;

        for (var i = _attempted.Count - 1; i >= 0; i--)
        {
            try
            {
                var service = _attempted[i];
                if (service is IAsyncDisposable disposable)
                    await disposable.DisposeAsync();
                else
                    await service.StopAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                (failures ??= new List<Exception>()).Add(ex);
            }
        }

        _attempted.Clear();

        if (failures != null)
            throw new AggregateException("One or more resources failed to shut down.", failures);
    }

    private static string describe(IHostedService service)
        => service is ITestResource resource
            ? $"Resource '{resource.Name}'"
            : $"Hosted service '{service.GetType().Name}'";
}
