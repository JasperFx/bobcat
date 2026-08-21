using Bobcat.Engine;

namespace Bobcat.Runtime;

/// <summary>
/// Orchestrates test resources: registration, lifecycle, and lookup.
/// Resources start in registration order, tear down in reverse.
/// </summary>
public class TestSuite : IAsyncDisposable
{
    private readonly List<ITestResource> _resources = new();
    private readonly Dictionary<string, ITestResource> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IGlobalAction> _globalActions = new();
    private readonly List<ITestResource> _attempted = new();

    /// <summary>
    /// Register cross-cutting setup/teardown that runs once for the whole test run.
    /// Resource-shaped work belongs in an <see cref="ITestResource"/> instead.
    /// </summary>
    public TestSuite AddGlobalAction(IGlobalAction action)
    {
        _globalActions.Add(action);
        return this;
    }

    public IReadOnlyList<IGlobalAction> GlobalActions => _globalActions;

    public void AddResource(ITestResource resource)
    {
        AddResource(resource.Name, resource);
    }

    public void AddResource(string name, ITestResource resource)
    {
        if (_byName.ContainsKey(name))
            throw new ArgumentException($"A resource named '{name}' is already registered.");

        _resources.Add(resource);
        _byName[name] = resource;
    }

    /// <summary>
    /// Start all resources in registration order. Any failure is catastrophic: the exception
    /// wraps in a <see cref="SpecCatastrophicException"/> naming the resource, and the
    /// resources after it are never asked to start. The ones before it are up, and the one
    /// that threw may be half up — <see cref="DisposeAsync"/> tears both down.
    /// </summary>
    public async Task StartAll()
    {
        foreach (var resource in _resources)
        {
            // Recorded before Start so a resource that throws part-way through — containers
            // up, health check failed — still gets its DisposeAsync.
            _attempted.Add(resource);

            try
            {
                await resource.Start();
            }
            catch (Exception ex)
            {
                throw new SpecCatastrophicException(
                    $"Resource '{resource.Name}' failed to start: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Reset all resources between scenarios.
    /// </summary>
    public async Task ResetAll()
    {
        foreach (var resource in _resources)
        {
            await resource.ResetBetweenScenarios();
        }
    }

    /// <summary>
    /// Run every global action's SetUp, in registration order. Called after
    /// <see cref="StartAll"/> so resources are available, and before the first feature.
    /// A failure here is catastrophic — nothing downstream can be trusted.
    /// </summary>
    public async Task RunGlobalSetUp()
    {
        foreach (var action in _globalActions)
        {
            try
            {
                await action.SetUp();
            }
            catch (Exception ex)
            {
                throw new SpecCatastrophicException(
                    $"Global action '{action.GetType().Name}' failed during SetUp: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Run every global action's TearDown, in reverse registration order. Called after the
    /// last feature and before resources are disposed. Every action gets a turn even if an
    /// earlier one threw; the first failure surfaces once they have all run.
    /// </summary>
    public async Task RunGlobalTearDown()
    {
        List<Exception>? failures = null;

        for (var i = _globalActions.Count - 1; i >= 0; i--)
        {
            try
            {
                await _globalActions[i].TearDown();
            }
            catch (Exception ex)
            {
                (failures ??= new List<Exception>()).Add(ex);
            }
        }

        if (failures != null)
            throw new AggregateException("One or more global actions failed during TearDown.", failures);
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

    public IReadOnlyList<ITestResource> Resources => _resources;

    /// <summary>
    /// Dispose every resource that <see cref="StartAll"/> started or tried to start, in reverse
    /// registration order. A resource that was never asked to start is not touched: its
    /// <c>DisposeAsync</c> was written assuming <c>Start</c> ran, and a second exception from
    /// tearing down something that never came up would only bury the one that matters.
    /// Every resource gets its turn even if an earlier one threw; the failures surface
    /// together once they have all run.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<Exception>? failures = null;

        for (var i = _attempted.Count - 1; i >= 0; i--)
        {
            try
            {
                await _attempted[i].DisposeAsync();
            }
            catch (Exception ex)
            {
                (failures ??= new List<Exception>()).Add(ex);
            }
        }

        _attempted.Clear();

        if (failures != null)
            throw new AggregateException("One or more resources failed to dispose.", failures);
    }
}
