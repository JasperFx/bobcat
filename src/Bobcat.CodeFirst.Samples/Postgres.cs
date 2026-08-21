using Npgsql;

namespace Bobcat.CodeFirst.Samples;

/// <summary>
/// Where these specs find their database, and whether one is actually there. The same contract as
/// <c>Bobcat.Marten.Tests.PostgresEnvironment</c>: <c>BOBCAT_POSTGRES</c> or the repo's
/// docker-compose instance on 5445; skip off CI when nothing answers, never on CI.
/// </summary>
public static class Postgres
{
    private static readonly Lazy<bool> _available = new(probe, isThreadSafe: true);

    public const string Default =
        "Host=localhost;Port=5445;Database=bobcat_test;Username=postgres;Password=postgres";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("BOBCAT_POSTGRES") is { Length: > 0 } configured
            ? configured
            : Default;

    public static bool IsCi =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsAvailable => _available.Value;

    public static string SkipReason =>
        $"Bobcat.CodeFirst.Samples: no Postgres reachable at {describe()}, so no scenarios are registered. " +
        "Run `docker compose up -d` from the repo root, or point BOBCAT_POSTGRES at your own database. " +
        "(This skip never applies on CI.)";

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
