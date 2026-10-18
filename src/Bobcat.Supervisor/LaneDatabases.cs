using System.Data.Common;

namespace Bobcat.Supervisor;

/// <summary>
/// One database per worker lane on PostgreSQL or SQL Server, created before the run and handed to
/// each worker as a complete connection string (bobcat#700) — the provisioning loop every adopter
/// wrote by hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>No database driver is referenced here.</b> The caller passes its own ADO.NET provider
/// (<c>NpgsqlFactory.Instance</c>, <c>SqlClientFactory.Instance</c>), which is all a
/// <see cref="DbProviderFactory"/> needs to be, so the supervisor stays free of any store.
/// </para>
/// <para>
/// <b>A lane's connection string is built, never stitched.</b> The administrative connection
/// string is the template: the provider's own connection string builder sets its database key to
/// the lane's database and leaves everything else alone. A text replacement is how a parallel run
/// once pointed every worker at one database when the base string changed under it
/// (see <see cref="WorkerConnectionStrings"/>).
/// </para>
/// <code>
/// var lanes = LaneDatabases.Postgres(NpgsqlFactory.Instance,
///     "Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=postgres", prefix: "marten_w");
/// await lanes.Recreate(workers);
///
/// new MtpWorkerFactory(executable)
/// {
///     ConnectionStrings = new WorkerConnectionStrings().Add("postgres", lanes.ForWorker)
/// };
/// </code>
/// <para>
/// Lane numbers run from 0 to <c>MaxParallelWorkers - 1</c>, and discovery, isolated and recycled
/// workers all report lane 0, so <c>Recreate(workers)</c> provisions every database a run uses.
/// </para>
/// </remarks>
public sealed class LaneDatabases
{
    private readonly DbProviderFactory _provider;
    private readonly string _adminConnectionString;
    private readonly Dialect _dialect;

    private LaneDatabases(DbProviderFactory provider, string adminConnectionString, string prefix, Dialect dialect)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminConnectionString);
        validatePrefix(prefix);

        _provider = provider;
        _adminConnectionString = adminConnectionString;
        _dialect = dialect;
        Prefix = prefix;
    }

    /// <summary>
    /// Lane databases on PostgreSQL. The administrative connection string names a database that
    /// already exists (usually <c>postgres</c>); each lane's string is the same with
    /// <c>Database</c> set to the lane's.
    /// </summary>
    /// <param name="provider">The Npgsql provider: <c>NpgsqlFactory.Instance</c>.</param>
    /// <param name="adminConnectionString">Connects to the server to create and drop databases, and is each lane's template.</param>
    /// <param name="prefix">The lane database's name before its lane number: <c>specs_w0</c>, <c>specs_w1</c>…</param>
    public static LaneDatabases Postgres(DbProviderFactory provider, string adminConnectionString, string prefix = "specs_w")
        => new(provider, adminConnectionString, prefix, Dialect.Postgres);

    /// <summary>
    /// Lane databases on SQL Server. The administrative connection string normally names
    /// <c>master</c>; each lane's string is the same with <c>Initial Catalog</c> set to the lane's.
    /// </summary>
    /// <param name="provider">The SqlClient provider: <c>SqlClientFactory.Instance</c>.</param>
    /// <param name="adminConnectionString">Connects to the server to create and drop databases, and is each lane's template.</param>
    /// <param name="prefix">The lane database's name before its lane number: <c>specs_w0</c>, <c>specs_w1</c>…</param>
    public static LaneDatabases SqlServer(DbProviderFactory provider, string adminConnectionString, string prefix = "specs_w")
        => new(provider, adminConnectionString, prefix, Dialect.SqlServer);

    /// <summary>The lane database's name before its lane number.</summary>
    public string Prefix { get; }

    /// <summary>The database lane <paramref name="lane"/> gets: <c>{Prefix}{lane}</c>.</summary>
    public string DatabaseFor(int lane)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lane);
        return Prefix + lane;
    }

    /// <summary>Lane <paramref name="lane"/>'s complete connection string.</summary>
    public string ConnectionStringFor(int lane)
    {
        var builder = _provider.CreateConnectionStringBuilder()
                      ?? throw new InvalidOperationException(
                          $"{_provider.GetType().Name} has no connection string builder, so a lane's connection string cannot be built.");

        builder.ConnectionString = _adminConnectionString;

        // Remove the spellings a builder may hold under its own synonym before setting the
        // canonical one, so the admin database cannot survive beside the lane's
        foreach (var key in _dialect.DatabaseKeys) builder.Remove(key);
        builder[_dialect.DatabaseKeys[0]] = DatabaseFor(lane);

        return builder.ConnectionString;
    }

    /// <summary>
    /// For <see cref="WorkerConnectionStrings.Add(string, Func{WorkerLaunchContext, string})"/>,
    /// or inside an <c>EnvironmentFor</c>: the worker's lane's connection string.
    /// </summary>
    public Func<WorkerLaunchContext, string> ForWorker => worker => ConnectionStringFor(worker.Lane);

    /// <summary>
    /// Drops and recreates the databases for lanes 0 to <paramref name="lanes"/> - 1, so every run
    /// starts empty. A database carrying the previous run's tables lets a schema change pass
    /// locally against stale tables and fail on CI's fresh one.
    /// </summary>
    /// <param name="lanes">The number of lanes: the supervisor's <c>MaxParallelWorkers</c>.</param>
    /// <param name="log">Says which database each lane got; never required.</param>
    public Task Recreate(int lanes, Action<string>? log = null, CancellationToken ct = default)
        => provision(lanes, recreate: true, log, ct);

    /// <summary>
    /// Creates whichever lane databases do not exist yet and leaves the rest as they are — for a
    /// suite whose tests reset their own schemas, where keeping a database saves its creation.
    /// </summary>
    /// <param name="lanes">The number of lanes: the supervisor's <c>MaxParallelWorkers</c>.</param>
    /// <param name="log">Says which database each lane got; never required.</param>
    public Task EnsureCreated(int lanes, Action<string>? log = null, CancellationToken ct = default)
        => provision(lanes, recreate: false, log, ct);

    private async Task provision(int lanes, bool recreate, Action<string>? log, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lanes, 1);

        // CREATE DATABASE cannot run inside the database it creates, nor inside a transaction, and
        // two workers must not race to create one, so this runs once, here, before any launches
        await using var connection = _provider.CreateConnection()
                                     ?? throw new InvalidOperationException($"{_provider.GetType().Name} created no connection.");
        connection.ConnectionString = _adminConnectionString;
        await connection.OpenAsync(ct);

        for (var lane = 0; lane < lanes; lane++)
        {
            var name = DatabaseFor(lane);
            var created = await _dialect.Provision(connection, name, recreate, ct);
            log?.Invoke($"lane {lane} -> {name}{(created ? "" : " (kept)")}");
        }
    }

    private static void validatePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        // The name is an identifier in DDL, which cannot be a parameter, so it is limited to what
        // needs no quoting on either server. Lower case because PostgreSQL folds an unquoted name.
        if (!(prefix[0] is >= 'a' and <= 'z' or '_') || !prefix.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
        {
            throw new ArgumentException(
                $"'{prefix}' cannot prefix a lane database: use lower-case letters, digits and '_', starting with a letter or '_'.",
                nameof(prefix));
        }

        // PostgreSQL truncates an identifier at 63 bytes; leave room for the lane number
        if (prefix.Length > 56)
        {
            throw new ArgumentException($"'{prefix}' is too long to prefix a lane database (56 characters at most).", nameof(prefix));
        }
    }

    private abstract class Dialect
    {
        public static readonly Dialect Postgres = new PostgresDialect();
        public static readonly Dialect SqlServer = new SqlServerDialect();

        /// <summary>The database key, canonical spelling first.</summary>
        public abstract string[] DatabaseKeys { get; }

        /// <summary>Provisions one database; false when it already existed and was kept.</summary>
        public abstract Task<bool> Provision(DbConnection connection, string name, bool recreate, CancellationToken ct);

        protected static async Task<object?> scalar(DbConnection connection, string sql, CancellationToken ct)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 120;
            return await command.ExecuteScalarAsync(ct);
        }

        protected static async Task execute(DbConnection connection, string sql, CancellationToken ct)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    // The name was validated to need no quoting, and an identifier in DDL cannot be a parameter

    private sealed class PostgresDialect : Dialect
    {
        public override string[] DatabaseKeys { get; } = ["Database", "DB"];

        public override async Task<bool> Provision(DbConnection connection, string name, bool recreate, CancellationToken ct)
        {
            if (recreate)
            {
                // WITH (FORCE), PostgreSQL 13 and later, ends the previous run's connections,
                // which a plain DROP would wait on
                await execute(connection, $"DROP DATABASE IF EXISTS {name} WITH (FORCE)", ct);
            }
            else if (await scalar(connection, $"SELECT 1 FROM pg_database WHERE datname = '{name}'", ct) is not (null or DBNull))
            {
                // PostgreSQL has no CREATE DATABASE IF NOT EXISTS
                return false;
            }

            await execute(connection, $"CREATE DATABASE {name}", ct);
            return true;
        }
    }

    private sealed class SqlServerDialect : Dialect
    {
        public override string[] DatabaseKeys { get; } = ["Initial Catalog", "Database"];

        public override async Task<bool> Provision(DbConnection connection, string name, bool recreate, CancellationToken ct)
        {
            var exists = await scalar(connection, $"SELECT DB_ID('{name}')", ct) is not (null or DBNull);
            if (exists && !recreate) return false;

            if (exists)
            {
                // SINGLE_USER WITH ROLLBACK IMMEDIATE ends the previous run's connections first
                await execute(connection, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];", ct);
            }

            await execute(connection, $"CREATE DATABASE [{name}]", ct);
            return true;
        }
    }
}
