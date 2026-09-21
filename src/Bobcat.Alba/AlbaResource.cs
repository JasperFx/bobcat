using Alba;
using Bobcat.Runtime;
using JasperFx.CommandLine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Bobcat.Alba;

/// <summary>
/// Marker so a step can reach the Alba host without naming the application's entry-point type.
/// </summary>
public interface IAlbaResource : IHostResource
{
    IAlbaHost AlbaHost { get; }
}

/// <summary>
/// An ASP.NET Core application hosted in memory by Alba, as a Bobcat test resource.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a rebuild, and its shape is evidence rather than design.</b> The original
/// <c>Bobcat.Alba</c> was deleted, every sample wrote the resource it actually needed, and all
/// nine came out byte-identical. That file is this one. What the samples never reached for —
/// a factory-delegate form, <c>Restart</c>, console log-level control, an explicit
/// <c>WithContentRoot</c>, raw-response helpers, the <c>IHttpResource</c> transport seam — is
/// not here, and should come back only when something needs it.
/// </para>
/// <para>
/// One thing is here that no sample asked for: <see cref="IHostResource"/>. The hand-written
/// copies implemented plain <see cref="ITestResource"/>, which silently cost them every
/// <c>IStepContext</c> helper in Bobcat that resolves a host — <c>Context.Host()</c>,
/// <c>GetService&lt;T&gt;()</c>, <c>EventStore()</c>, the EF Core recipe — and meant
/// <c>TestResources.BeginScenarioAll</c> opened no per-scenario DI scope for them at all. The
/// duplication hid a capability regression; a resource that wraps an <c>IHost</c> should say so.
/// </para>
/// </remarks>
public class AlbaResource<TProgram> : IAlbaResource where TProgram : class
{
    private readonly Func<IAlbaHost, Task>? _reset;
    private readonly ScenarioScope _scope;
    private IAlbaHost? _host;

    public AlbaResource(Func<IAlbaHost, Task>? reset = null, string? name = null)
    {
        _reset = reset;
        Name = name ?? typeof(TProgram).Name;
        _scope = new ScenarioScope(Name, () => _host?.Services);
    }

    public string Name { get; }

    public IAlbaHost AlbaHost => _host
        ?? throw new InvalidOperationException(
            $"Alba resource '{Name}' has not been started, so its host is not available yet.");

    public IHost Host => AlbaHost;

    public IServiceProvider RootServices => _scope.Root;
    public IServiceProvider CurrentServices => _scope.Current;

    public async Task StartAsync(CancellationToken cancellationToken = default)
        => _host = await boot();

    private static async Task<IAlbaHost> boot()
    {
        // A JasperFx host built by WebApplicationFactory would otherwise run its command line and
        // never hand the host back.
        if (!JasperFxEnvironment.AutoStartHost) JasperFxEnvironment.AutoStartHost = true;

        // WebApplicationFactory guesses the content root as <solution>/<assembly name>, which is
        // wrong for every project that does not sit directly under the solution.
        var contentRoot = AlbaContentRoot.Resolve(typeof(TProgram).Assembly);

        return contentRoot.Path is { } path
            ? await global::Alba.AlbaHost.For<TProgram>(builder => builder.UseContentRoot(path))
            : await global::Alba.AlbaHost.For<TProgram>();
    }

    public Task ResetBetweenScenarios() => _reset?.Invoke(AlbaHost) ?? Task.CompletedTask;

    public ValueTask BeginScenarioScope() => _scope.Begin();

    public ValueTask EndScenarioScope() => _scope.End();

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _scope.End();

        var host = _host;
        _host = null;
        if (host != null) await host.DisposeAsync();
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));
}
