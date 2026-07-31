using Npgsql;
using Xunit;

namespace Bobcat.Marten.Tests;

/// <summary>
/// Where the Marten integration tests find their database, and whether one is actually there.
/// </summary>
public static class PostgresEnvironment
{
    private static readonly Lazy<bool> _available = new(probe, isThreadSafe: true);

    /// <summary>
    /// Points at the repo's docker-compose Postgres, which publishes 5445 so it never collides
    /// with a Postgres the developer already runs on 5432 (or 5433).
    /// </summary>
    public const string Default =
        "Host=localhost;Port=5445;Database=bobcat_test;Username=postgres;Password=postgres";

    /// <summary>
    /// The connection string, from <c>BOBCAT_POSTGRES</c> or the docker-compose default.
    /// </summary>
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("BOBCAT_POSTGRES") is { Length: > 0 } configured
            ? configured
            : Default;

    /// <summary>GitHub Actions (and most CI systems) set CI=true.</summary>
    public static bool IsCi =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsAvailable => _available.Value;

    public static string SkipReason =>
        $"No Postgres reachable at {describe()}. Run `docker compose up -d` from the repo root, " +
        "or point BOBCAT_POSTGRES at your own database. (This skip never applies on CI.)";

    private static bool probe()
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString) { Timeout = 3 };
            using var connection = new NpgsqlConnection(builder.ConnectionString);
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Host/database only — never echo the password into test output.</summary>
    private static string describe()
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
            return $"{builder.Host}:{builder.Port}/{builder.Database}";
        }
        catch
        {
            return "the configured connection string";
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for tests that need a real Postgres.
///
/// <para>Off CI, a missing database skips the test with an actionable message so a fresh clone
/// still runs green without Docker. On CI the skip is deliberately NOT applied — the workflow
/// provides a Postgres service, and a missing database there has to fail the build rather than
/// quietly report a pass.</para>
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresEnvironment.IsCi && !PostgresEnvironment.IsAvailable)
        {
            Skip = PostgresEnvironment.SkipReason;
        }
    }
}
