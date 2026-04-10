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
    /// Start all resources in registration order. Any failure is catastrophic.
    /// </summary>
    public async Task StartAll()
    {
        foreach (var resource in _resources)
        {
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
    /// Dispose all resources in reverse registration order.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        for (var i = _resources.Count - 1; i >= 0; i--)
        {
            await _resources[i].DisposeAsync();
        }
    }
}
