using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// A database per lane, provisioned through whatever ADO.NET provider the caller hands over
/// (bobcat#700). Driven through a recording provider: the supervisor references no driver, and
/// the statements are what differ between PostgreSQL and SQL Server.
/// </summary>
public class LaneDatabasesTests
{
    private const string PostgresAdmin = "Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=postgres";
    private const string SqlServerAdmin = "Server=localhost,1434;User Id=sa;Password=P@55w0rd;Initial Catalog=master;Encrypt=False";

    [Fact]
    public void a_lanes_connection_string_is_the_template_with_its_own_database()
    {
        var lanes = LaneDatabases.Postgres(new RecordingProvider(), PostgresAdmin, "marten_w");

        var lane1 = new DbConnectionStringBuilder { ConnectionString = lanes.ConnectionStringFor(1) };

        lane1["Database"].ShouldBe("marten_w1");
        lane1["Host"].ShouldBe("localhost");
        lane1["Port"].ShouldBe("5433");
        lane1["Password"].ShouldBe("postgres");
    }

    [Fact]
    public void a_synonym_for_the_database_key_does_not_survive_beside_the_lanes()
    {
        var lanes = LaneDatabases.SqlServer(new RecordingProvider(), "Server=localhost;Database=master;User Id=sa", "specs_w");

        var lane0 = new DbConnectionStringBuilder { ConnectionString = lanes.ConnectionStringFor(0) };

        lane0["Initial Catalog"].ShouldBe("specs_w0");
        lane0.ContainsKey("Database").ShouldBeFalse();
    }

    [Fact]
    public void for_worker_reads_the_lane_off_the_launch()
    {
        var lanes = LaneDatabases.Postgres(new RecordingProvider(), PostgresAdmin);

        lanes.ForWorker(new WorkerLaunchContext(2, WorkerPurpose.Lane)).ShouldContain("specs_w2");
    }

    [Fact]
    public void it_plugs_into_worker_connection_strings()
    {
        var lanes = LaneDatabases.Postgres(new RecordingProvider(), PostgresAdmin);
        var connectionStrings = new WorkerConnectionStrings().Add("postgres", lanes.ForWorker);

        connectionStrings.EnvironmentFor(new WorkerLaunchContext(3, WorkerPurpose.Lane))["ConnectionStrings__postgres"]
            .ShouldBe(lanes.ConnectionStringFor(3));
    }

    [Theory]
    [InlineData("Specs_w")]
    [InlineData("1specs")]
    [InlineData("specs-w")]
    [InlineData("specs; DROP DATABASE postgres; --")]
    [InlineData("")]
    public void a_prefix_that_would_need_quoting_is_refused(string prefix)
    {
        Should.Throw<ArgumentException>(() => LaneDatabases.Postgres(new RecordingProvider(), PostgresAdmin, prefix));
    }

    [Fact]
    public async Task recreate_on_postgres_drops_each_lane_forcing_its_connections_off_then_creates_it()
    {
        var provider = new RecordingProvider();
        var log = new List<string>();

        await LaneDatabases.Postgres(provider, PostgresAdmin, "specs_w").Recreate(2, log.Add, TestContext.Current.CancellationToken);

        provider.Opened.ShouldBe([PostgresAdmin]);
        provider.Statements.ShouldBe([
            "DROP DATABASE IF EXISTS specs_w0 WITH (FORCE)",
            "CREATE DATABASE specs_w0",
            "DROP DATABASE IF EXISTS specs_w1 WITH (FORCE)",
            "CREATE DATABASE specs_w1"
        ]);
        log.ShouldBe(["lane 0 -> specs_w0", "lane 1 -> specs_w1"]);
    }

    [Fact]
    public async Task ensure_created_on_postgres_creates_only_the_missing_lanes()
    {
        var provider = new RecordingProvider { Scalar = sql => sql.Contains("'specs_w0'") ? 1 : null };
        var log = new List<string>();

        await LaneDatabases.Postgres(provider, PostgresAdmin).EnsureCreated(2, log.Add, TestContext.Current.CancellationToken);

        provider.Statements.ShouldBe([
            "SELECT 1 FROM pg_database WHERE datname = 'specs_w0'",
            "SELECT 1 FROM pg_database WHERE datname = 'specs_w1'",
            "CREATE DATABASE specs_w1"
        ]);
        log.ShouldBe(["lane 0 -> specs_w0 (kept)", "lane 1 -> specs_w1"]);
    }

    [Fact]
    public async Task recreate_on_sql_server_takes_an_existing_catalog_single_user_before_dropping_it()
    {
        var provider = new RecordingProvider { Scalar = sql => sql.Contains("'specs_w0'") ? 5 : DBNull.Value };

        await LaneDatabases.SqlServer(provider, SqlServerAdmin).Recreate(2, ct: TestContext.Current.CancellationToken);

        provider.Opened.ShouldBe([SqlServerAdmin]);
        provider.Statements.ShouldBe([
            "SELECT DB_ID('specs_w0')",
            "ALTER DATABASE [specs_w0] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [specs_w0];",
            "CREATE DATABASE [specs_w0]",
            "SELECT DB_ID('specs_w1')",
            "CREATE DATABASE [specs_w1]"
        ]);
    }

    [Fact]
    public async Task ensure_created_on_sql_server_keeps_an_existing_catalog()
    {
        var provider = new RecordingProvider { Scalar = _ => 5 };

        await LaneDatabases.SqlServer(provider, SqlServerAdmin).EnsureCreated(1, ct: TestContext.Current.CancellationToken);

        provider.Statements.ShouldBe(["SELECT DB_ID('specs_w0')"]);
    }

    [Fact]
    public async Task no_lanes_is_refused()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => LaneDatabases.Postgres(new RecordingProvider(), PostgresAdmin).Recreate(0));
    }

    // ---------------------------------------------------------------- a provider that records

    private sealed class RecordingProvider : DbProviderFactory
    {
        public List<string> Opened { get; } = [];
        public List<string> Statements { get; } = [];
        public Func<string, object?> Scalar { get; init; } = _ => null;

        public override DbConnection CreateConnection() => new RecordingConnection(this);
        public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new();
    }

    private sealed class RecordingConnection(RecordingProvider provider) : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() => provider.Opened.Add(ConnectionString);
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new RecordingCommand(provider);
    }

    private sealed class RecordingCommand(RecordingProvider provider) : DbCommand
    {
        [AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();

        public override int ExecuteNonQuery()
        {
            provider.Statements.Add(CommandText);
            return 0;
        }

        public override object? ExecuteScalar()
        {
            provider.Statements.Add(CommandText);
            return provider.Scalar(CommandText);
        }
    }
}
