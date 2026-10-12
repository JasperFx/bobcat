using Microsoft.Extensions.Hosting;

namespace Bobcat.Runtime;

/// <summary>
/// A named test resource — database, IHost, Docker container, message broker, etc.
/// </summary>
/// <remarks>
/// <para>
/// A resource is an <see cref="IHostedService"/>, so its two lifecycle verbs are the ones every
/// .NET developer already knows — <c>StartAsync</c> and <c>StopAsync</c> — and a resource is
/// registrable as a hosted service wherever one is wanted. Bobcat adds the two verbs a host has
/// no notion of: <see cref="ResetBetweenScenarios"/> and <see cref="Check"/>.
/// </para>
/// <para>
/// <strong>Bobcat does not own a resource's lifecycle in a test process.</strong> Specs are
/// ordinary xUnit / TUnit / NUnit / MSTest tests, so whatever fixture holds the resource (a class
/// or assembly fixture, a TUnit hook) starts it, resets it and stops it. Bobcat drives a resource
/// in two places only, both in the supervisor: a resource registered with
/// <c>Supervisor.AddRecyclableResource</c> has its <see cref="Check"/> run during preflight, before
/// any worker launches, and is recycled between attempts when a failure policy asks for it.
/// </para>
/// <para>
/// <strong>Teardown is <c>StopAsync</c>, not <c>DisposeAsync</c>.</strong> The default
/// <see cref="IAsyncDisposable.DisposeAsync"/> here delegates to it, so a resource that owns
/// nothing beyond what <c>StopAsync</c> releases need not write one at all, and
/// <c>await using</c> over such a resource still tears it down. Write your own
/// <c>DisposeAsync</c> only for handles <c>StopAsync</c> leaves open — and call
/// <c>StopAsync</c> from it, because an <c>await using</c> or a fixture's disposal calls
/// <c>DisposeAsync</c>, not <c>StopAsync</c>.
/// </para>
/// </remarks>
public interface ITestResource : IHostedService, IAsyncDisposable
{
    /// <summary>
    /// Unique name for this resource. Used for lookup when multiple resources
    /// of the same type exist (e.g., two Alba hosts for cross-service testing).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Reset state between scenarios (truncate tables, purge queues, clear tracked sessions,
    /// etc). Bobcat does not call this itself; the fixture holding the resource does.
    /// </summary>
    Task ResetBetweenScenarios();

    /// <summary>
    /// Validate that this resource is usable, throwing with a diagnostic message if not.
    /// The supervisor runs this during preflight for every resource registered with it, before
    /// any worker launches.
    /// </summary>
    /// <remarks>
    /// Default is a no-op so existing resources are unaffected. Deliberately mirrors
    /// <c>JasperFx.Resources.IStatefulResource.Check</c> — same verb, same "throw to fail"
    /// contract — so a resource can satisfy both without adapting.
    /// </remarks>
    Task Check(CancellationToken token) => Task.CompletedTask;

    /// <summary>
    /// Teardown runs through <see cref="IHostedService.StopAsync"/>. Override only to release
    /// something <c>StopAsync</c> does not, and call <c>StopAsync</c> when you do.
    /// </summary>
    ValueTask IAsyncDisposable.DisposeAsync() => new(StopAsync(CancellationToken.None));
}

/// <summary>
/// A resource whose state cannot be reliably drained the way a database is truncated — message
/// brokers, mostly. Recycling throws the underlying container or process away and stands a fresh
/// one up.
/// </summary>
/// <remarks>
/// <para>
/// <c>Recycle</c> is deliberately a third verb alongside <see cref="ITestResource.ResetBetweenScenarios"/>
/// (clean the state) and <c>StopAsync</c> (final teardown). Reset assumes the thing still
/// works; recycle assumes it does not.
/// </para>
/// <para>
/// <strong>Recyclable resources belong to the supervisor, not a worker.</strong> A worker cannot
/// restart the broker it is about to be replaced alongside, which is the whole reason
/// <c>RetryAfterRecycle</c> lives above the process boundary. Register these with
/// <c>Supervisor.AddRecyclableResource</c>.
/// </para>
/// </remarks>
public interface IRecyclableResource : ITestResource
{
    /// <summary>Throw the underlying container/process away and stand a fresh one up.</summary>
    Task Recycle(CancellationToken token = default);
}

/// <summary>
/// A resource that can stop and start again <em>inside</em> a scenario, on purpose, as part of
/// what the scenario is testing — "the application restarts and forgets nothing", "a queued
/// message survives a bounce". Hosts, mostly.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a fourth verb, not a reuse of <see cref="IRecyclableResource.Recycle"/>. Recycle
/// assumes the resource is broken and is decided by the supervisor between attempts, outside any
/// scenario; <c>Restart</c> assumes the resource is healthy and is called by a step, mid-scenario,
/// because the spec says so. Conflating them would let the supervisor "recycle" an in-process
/// host it cannot see, and would make a spec's restart step read as a recovery action.
/// </para>
/// <para>
/// The resource keeps its name and identity across a restart, so a test holding it keeps working,
/// but the services behind it are new. If the resource keeps a per-scenario DI scope, it should
/// close that scope before the restart and open a fresh one on the new host afterward. Anything a
/// test captured from the old host (a scope, a session, a client) is dead after a restart —
/// re-resolve, don't hoard.
/// </para>
/// <para>
/// A restart is not a reset: <see cref="ITestResource.ResetBetweenScenarios"/> does not run,
/// because the whole point is usually to prove that persistent state <em>survives</em>.
/// </para>
/// </remarks>
public interface IRestartableResource : ITestResource
{
    /// <summary>
    /// Stop the underlying host and start a fresh one. Throws if the resource has not been
    /// started.
    /// </summary>
    Task Restart(CancellationToken token = default);
}
