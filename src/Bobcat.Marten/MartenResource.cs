using Marten;

namespace Bobcat.Runtime;

/// <summary>
/// Marker interface so extension methods can locate a Marten store without knowing the
/// concrete resource type — mirrors <c>IAlbaResource</c>/<c>IHostResource</c>.
/// </summary>
public interface IMartenResource : ITestResource
{
    IDocumentStore DocumentStore { get; }
}

/// <summary>
/// A test resource that wraps a Marten <see cref="IDocumentStore"/> for use in specs.
/// Built from a user-supplied factory (or an existing store). By default each scenario
/// starts against freshly cleaned data; override the reset to customize.
/// </summary>
public class MartenResource : IMartenResource
{
    private readonly Func<Task<IDocumentStore>> _factory;
    private readonly Func<IDocumentStore, Task>? _reset;
    private IDocumentStore? _store;

    public IDocumentStore DocumentStore => _store
        ?? throw new InvalidOperationException($"MartenResource '{Name}' has not been started.");

    public string Name { get; }

    public MartenResource(Func<Task<IDocumentStore>> factory, string? name = null, Func<IDocumentStore, Task>? reset = null)
    {
        _factory = factory;
        Name = name ?? "Marten";
        _reset = reset;
    }

    public MartenResource(Func<IDocumentStore> factory, string? name = null, Func<IDocumentStore, Task>? reset = null)
        : this(() => Task.FromResult(factory()), name, reset)
    {
    }

    public MartenResource(IDocumentStore store, string? name = null, Func<IDocumentStore, Task>? reset = null)
        : this(() => Task.FromResult(store), name, reset)
    {
    }

    public async Task Start()
    {
        _store = await _factory();
    }

    /// <summary>
    /// Defaults to cleaning all document and event data so each scenario starts fresh
    /// (schema is preserved). Supply a custom reset to override.
    /// </summary>
    public async Task ResetBetweenScenarios()
    {
        if (_reset != null)
        {
            await _reset(DocumentStore);
            return;
        }

        await DocumentStore.Advanced.Clean.DeleteAllDocumentsAsync();
        await DocumentStore.Advanced.Clean.DeleteAllEventDataAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_store != null)
            await _store.DisposeAsync();
    }
}
