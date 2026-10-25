using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Bobcat.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// #region assembly-fixture-registration
// One instance for the whole assembly: the AppHost (and its containers) start once per test
// process, however many spec classes ask for it.
[assembly: AssemblyFixture(typeof(Bobcat.Aspire.Samples.TestInfrastructure))]
// #endregion assembly-fixture-registration

namespace Bobcat.Aspire.Samples;

/// <summary>
/// Starts the Aspire AppHost that owns this suite's Docker dependencies, once, and exposes the
/// connection strings the system under test needs. The docs' example (bobcat#769).
/// </summary>
// #region test-infrastructure
public sealed class TestInfrastructure : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);

    private DistributedApplication? _app;

    /// <summary>The whole connection string to the "orders" database, as the AppHost declared it.</summary>
    public string OrdersConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        // A supervised run with a database per lane hands each worker its own string, the Aspire
        // way (ConnectionStrings__orders). Use it, and start nothing.
        var handedDown = Environment.GetEnvironmentVariable("ConnectionStrings__orders");
        if (!string.IsNullOrEmpty(handedDown))
        {
            OrdersConnectionString = handedDown;
            return;
        }

        using var timeout = new CancellationTokenSource(StartupTimeout);

        try
        {
            var appHost = await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.Bobcat_Aspire_Samples_AppHost>(timeout.Token);

            // Aspire forwards every container's output, and every health check it retries while a
            // container boots, to the console; Postgres alone is a hundred lines, some on stderr.
            // Remove these two filters when a resource won't start and you need to see why.
            appHost.Services.AddLogging(logging => logging
                .SetMinimumLevel(LogLevel.Warning)
                .AddFilter($"{appHost.Environment.ApplicationName}.Resources", LogLevel.None)
                .AddFilter("Microsoft.Extensions.Diagnostics.HealthChecks", LogLevel.None));

            _app = await appHost.BuildAsync(timeout.Token);
            await _app.StartAsync(timeout.Token);

            // "Running" is not "ready": wait for the health check Aspire attaches to the database.
            await _app.ResourceNotifications.WaitForResourceHealthyAsync("orders", timeout.Token);

            OrdersConnectionString = await _app.GetConnectionStringAsync("orders", timeout.Token)
                ?? throw new InvalidOperationException("The AppHost has no connection string for 'orders'.");
        }
        catch (Exception e)
        {
            // Don't leave half-started containers behind.
            if (_app is not null) await _app.DisposeAsync();
            _app = null;

            // Nothing in this assembly can pass without its database. A catastrophic failure halts
            // every scenario after it with this one cause, and a supervised run aborts rather than
            // retrying (docs/marker-steps.md#environment-lost).
            throw new SpecCatastrophicException(
                $"The Aspire test infrastructure did not start (is Docker running?): {e.Message}", e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Stops the containers the AppHost started.
        if (_app is not null) await _app.DisposeAsync();
    }
}
// #endregion test-infrastructure
