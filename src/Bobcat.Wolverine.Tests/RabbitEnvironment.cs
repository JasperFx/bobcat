using System.Net.Sockets;

namespace Bobcat.Wolverine.Tests;

/// <summary>
/// The repo's docker-compose RabbitMQ, and the skip rule for tests that need a <b>real</b> broker
/// (issue #282). Mirrors <c>PostgresEnvironment</c> deliberately — same default-plus-override,
/// same "never skip on CI" rule — so there is one story about optional infrastructure rather than
/// two.
/// </summary>
public static class RabbitEnvironment
{
    private static readonly Lazy<bool> _available = new(probe, isThreadSafe: true);

    /// <summary>Published on 5683 so it never fights a RabbitMQ already running on 5672.</summary>
    public const string DefaultHost = "localhost";

    public const int DefaultPort = 5683;

    public static string Host =>
        Environment.GetEnvironmentVariable("BOBCAT_RABBIT_HOST") is { Length: > 0 } host ? host : DefaultHost;

    public static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("BOBCAT_RABBIT_PORT"), out var port) ? port : DefaultPort;

    public static bool IsCi =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsAvailable => _available.Value;

    public static string SkipReason =>
        $"No RabbitMQ reachable at {Host}:{Port}. Run `docker compose up -d` from the repo root, or "
        + "point BOBCAT_RABBIT_HOST / BOBCAT_RABBIT_PORT at your own broker. "
        + "(This skip never applies on CI.)";

    private static bool probe()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(3)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> for tests that need a real broker.
/// </summary>
/// <remarks>
/// Off CI a missing broker skips with an actionable message, so a fresh clone still runs green
/// without Docker. On CI the skip is deliberately NOT applied: the workflow provides the service,
/// and the whole point of issue #282 is that an absent broker is how this class of defect stays
/// invisible — a silent pass here would rebuild the blind spot the test exists to close.
/// </remarks>
public sealed class RabbitFactAttribute : FactAttribute
{
    public RabbitFactAttribute()
    {
        if (!RabbitEnvironment.IsCi && !RabbitEnvironment.IsAvailable)
        {
            Skip = RabbitEnvironment.SkipReason;
        }
    }
}
