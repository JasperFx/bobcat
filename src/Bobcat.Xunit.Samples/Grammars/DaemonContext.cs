namespace Bobcat.Xunit.Samples.Grammars;

/// <summary>
/// The shape of Marten's <c>DaemonContext</c>, the first real subject of marker comments and step
/// helpers (JasperFx/marten#5363): a base class of test helpers that a suite already calls, two of them
/// decorated so every call renders as a step. An in-memory stand-in for the async daemon, so the
/// <c>docs/marker-steps.md</c> examples run.
/// </summary>
public abstract class DaemonContext
{
    private readonly List<int> _published = new();
    private readonly List<int> _projected = new();
    private int _flushed;

    /// <summary>The event the projection fails on, or null when every event projects.</summary>
    protected int? PoisonEvent { get; set; }

    // #region step-helper-template
    [Given("the events are published on {threads} threads")]
    internal Task PublishMultiThreaded(int threads)
        => Task.WhenAll(Enumerable.Range(0, threads).Select(thread => Task.Run(() =>
        {
            lock (_published) _published.AddRange(Enumerable.Range(thread * 10 + 1, 10));
        })));
    // #endregion step-helper-template

    [Given("the events are flushed")]
    internal async Task FlushEvents()
    {
        await Task.Delay(10);
        _flushed = _published.Count;
    }

    /// <summary>Not decorated: a helper the suite calls that renders no step of its own.</summary>
    protected async Task PublishEvents()
    {
        await PublishMultiThreaded(3);
        await FlushEvents();
    }

    protected Task WaitForNonStaleAsync() => Task.Delay(5);

    protected Task<IDisposable> StartDaemon()
    {
        foreach (var e in _published.Take(_flushed).Order())
        {
            if (e == PoisonEvent) break;
            _projected.Add(e);
        }

        return Task.FromResult<IDisposable>(new NoDaemon());
    }

    protected Task CheckAllExpectedAggregatesAgainstActuals()
    {
        if (_projected.Count != _flushed)
            throw new InvalidOperationException($"expected {_flushed} projected events, found {_projected.Count}");

        return Task.CompletedTask;
    }

    protected Task CheckDocuments()
    {
        var expected = _published.Order().TakeWhile(e => e != PoisonEvent).Count();
        if (_projected.Count != expected)
            throw new InvalidOperationException($"expected {expected} documents, found {_projected.Count}");

        return Task.CompletedTask;
    }

    private sealed class NoDaemon : IDisposable
    {
        public void Dispose() { }
    }
}
