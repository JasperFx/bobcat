using Fisher;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.Hosting;

namespace Bobcat.CritterStack.ScenarioTests;

/// <summary>
/// An application with an event store and nothing else: no Wolverine, no message bus. One per store, shared
/// by a test class; every test uses fresh stream ids, so they never see each other's events.
/// </summary>
public abstract class StoreHost : IAsyncLifetime
{
    private IHost? _host;

    /// <summary>The host, once started. Null when the store is not available here and the test skips.</summary>
    public IHost Host => _host ?? throw new InvalidOperationException("The store host did not start");

    /// <summary>Whether this store can run here. Postgres is skipped locally when absent, never on CI.</summary>
    public virtual bool IsAvailable => true;

    public virtual string SkipReason => "";

    protected abstract IHost Build();

    public async ValueTask InitializeAsync()
    {
        if (!IsAvailable) return;

        await BeforeStartAsync();
        try
        {
            _host = Build();
            await _host.StartAsync();
            await AfterStartAsync();
        }
        finally
        {
            AfterStartAttempt();
        }
    }

    /// <summary>Runs whether or not the host started.</summary>
    protected virtual void AfterStartAttempt()
    {
    }

    protected virtual Task BeforeStartAsync() => Task.CompletedTask;

    protected virtual Task AfterStartAsync() => Task.CompletedTask;

    public virtual async ValueTask DisposeAsync()
    {
        if (_host is null) return;

        await _host.StopAsync();
        _host.Dispose();
    }
}

public sealed class MartenStoreHost : StoreHost
{
    private static int _hosts;

    // And one host building its schema at a time: Postgres races on its own catalog (pg_type) when two
    // schemas' DDL runs at once
    private static readonly SemaphoreSlim _ddl = new(1, 1);

    // A schema per host: the test classes' hosts start in parallel, and Marten's DDL races on one schema.
    // Dropped before (a previous run's leftovers) and after.
    private readonly string _schema = $"bobcat_cs_scenario_{Interlocked.Increment(ref _hosts)}";

    public override bool IsAvailable => PostgresEnvironment.IsCi || PostgresEnvironment.IsAvailable;

    public override string SkipReason => PostgresEnvironment.SkipReason;

    protected override IHost Build()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddMarten(opts =>
            {
                opts.Connection(PostgresEnvironment.ConnectionString);
                opts.DatabaseSchemaName = _schema;
                opts.AutoCreateSchemaObjects = AutoCreate.All;
                opts.DisableNpgsqlLogging = true;

                opts.Projections.Snapshot<Wallet>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<WalletSummary>(SnapshotLifecycle.Async);

                opts.Events.RegisterTagType<ScreeningId>("screening").ForAggregate<SeatAvailability>();
                opts.Projections.LiveStreamAggregation<SeatAvailability>();
            })
            // The schema before the daemon starts: applied after, the two race on the same DDL
            .ApplyAllDatabaseChangesOnStartup()
            .AddAsyncDaemon(DaemonMode.Solo);

        return builder.Build();
    }

    protected override async Task BeforeStartAsync()
    {
        await _ddl.WaitAsync();
        await dropSchemaAsync();
    }

    protected override void AfterStartAttempt() => _ddl.Release();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (IsAvailable) await dropSchemaAsync();
    }

    private async Task dropSchemaAsync()
    {
        await using var connection = new Npgsql.NpgsqlConnection(PostgresEnvironment.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS {_schema} CASCADE";
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class FisherStoreHost : StoreHost
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"bobcat-critterstack-scenario-{Guid.NewGuid():N}.db");

    protected override IHost Build()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddFisher(opts =>
            {
                opts.Connection($"Data Source={_file}");

                opts.Projections.Snapshot<Wallet>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<WalletSummary>(SnapshotLifecycle.Async);

                opts.Events.RegisterTagType<ScreeningId>("screening").ForAggregate<SeatAvailability>();
                opts.Projections.LiveStreamAggregation<SeatAvailability, Guid>();
            })
            // Fisher builds its schema lazily, and the daemon reads its progression table on start
            .ApplyAllDatabaseChangesOnStartup()
            .AddAsyncDaemon(DaemonMode.Solo);

        return builder.Build();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        foreach (var file in new[] { _file, _file + "-wal", _file + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A temp file; the OS reclaims it
            }
        }
    }
}
