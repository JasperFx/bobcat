using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Bobcat.Runtime;

/// <summary>
/// Containers from a <c>docker-compose.yml</c>, started before the run and recyclable during it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Readiness comes from the compose file, not from Bobcat.</strong> Docker already has a
/// mechanism for "is this container usable yet" — the <c>healthcheck</c> block — and it lives
/// where the container is defined, next to the credentials and the port mapping that the probe
/// needs. Shipping a Bobcat package per popular image would be re-describing, worse, something
/// the compose file already says:
/// </para>
/// <code>
/// healthcheck:
///   test: ["CMD-SHELL", "$$SQLCMD_PATH -S localhost -U sa -P $$SA_PASSWORD -C -Q \"SELECT 1\""]
///   interval: 10s
///   start_period: 20s
/// </code>
/// <para>
/// So this class knows nothing about SQL Server, Postgres or RabbitMQ. Readiness is decided in
/// four descending tiers, and the tier actually used is reported so a green run never hides the
/// fact that it only ever checked the process was alive:
/// </para>
/// <list type="number">
/// <item><see cref="Probe"/>, when the caller supplies one — a real query beats any proxy.</item>
/// <item>The service's declared Docker healthcheck.</item>
/// <item>A TCP connect to <see cref="ReadyWhenListeningOn"/>, when given.</item>
/// <item>Otherwise "the container is running", which is weak and says so.</item>
/// </list>
/// </remarks>
public sealed class DockerComposeResource : IRecyclableResource
{
    private readonly List<string> _composeFiles = new();

    public DockerComposeResource(string name) => Name = name;

    public string Name { get; }

    /// <summary>Compose files, in <c>-f</c> order. Defaults to compose's own discovery.</summary>
    public IReadOnlyList<string> ComposeFiles => _composeFiles;

    public DockerComposeResource UsingComposeFile(string path)
    {
        _composeFiles.Add(path);
        return this;
    }

    /// <summary>Services to manage. Empty means every service in the file.</summary>
    public IReadOnlyList<string> Services { get; init; } = [];

    /// <summary>Where <c>docker compose</c> runs. Defaults to the current directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>How long to wait for containers to become ready.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// A real readiness check — opening a connection, running a query. Beats every other tier
    /// because it tests the thing the tests actually do.
    /// </summary>
    public Func<CancellationToken, Task>? Probe { get; init; }

    /// <summary>A published port to TCP-connect to when no healthcheck is declared.</summary>
    public int? ReadyWhenListeningOn { get; init; }

    /// <summary>
    /// Whether to <c>docker compose down</c> at the end. Off by default: leaving containers up
    /// between runs is what makes the second run fast, and is what a developer expects locally.
    /// CI throws the whole machine away regardless.
    /// </summary>
    public bool StopOnDispose { get; init; }

    /// <summary>Progress, for a console or a log.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>How readiness was last established. Reported so a weak check is never silent.</summary>
    public string? ReadinessSource { get; private set; }

    public async Task Start()
    {
        Log?.Invoke($"docker compose up ({Name})");

        // --wait makes compose itself block until services are running or healthy, so the polling
        // loop every CI script hand-writes is not ours to write.
        await compose(
            ["up", "-d", "--wait", "--wait-timeout", ((int)StartTimeout.TotalSeconds).ToString(), .. Services],
            StartTimeout,
            CancellationToken.None);

        await Check(CancellationToken.None);
    }

    /// <summary>
    /// Recreates the containers from their images and waits for them to come back.
    /// </summary>
    /// <remarks>
    /// <c>--force-recreate</c> rather than <c>restart</c>, because recycling means throwing the
    /// thing away. A broker whose in-flight state cannot be drained is exactly the case this
    /// exists for, and restarting the process would keep that state.
    /// </remarks>
    public async Task Recycle(CancellationToken token = default)
    {
        Log?.Invoke($"docker compose recreate ({Name})");

        await compose(
            ["up", "-d", "--force-recreate", "--wait", "--wait-timeout",
                ((int)StartTimeout.TotalSeconds).ToString(), .. Services],
            StartTimeout,
            token);

        await Check(token);
    }

    /// <summary>
    /// Throws with a diagnostic when the containers are not usable. Satisfies both
    /// <see cref="Preflight"/> and JasperFx's <c>IStatefulResource.Check</c> contract.
    /// </summary>
    public async Task Check(CancellationToken token)
    {
        if (Probe is not null)
        {
            await Probe(token);
            ReadinessSource = "probe";
            return;
        }

        var containers = await inspect(token);
        if (containers.Count == 0)
        {
            throw new InvalidOperationException(
                $"Resource '{Name}': docker compose reports no containers for " +
                $"{(Services.Count == 0 ? "this compose file" : string.Join(", ", Services))}. " +
                "Has 'docker compose up' run, and is the Docker daemon reachable?");
        }

        var unhealthy = containers.Where(c => c.Health == "unhealthy").ToList();
        if (unhealthy.Count > 0)
        {
            throw new InvalidOperationException(
                $"Resource '{Name}': {string.Join(", ", unhealthy.Select(c => $"{c.Service} is unhealthy"))}. " +
                "See 'docker compose logs' and the healthcheck in the compose file.");
        }

        var notRunning = containers.Where(c => !c.State.Equals("running", StringComparison.OrdinalIgnoreCase)).ToList();
        if (notRunning.Count > 0)
        {
            throw new InvalidOperationException(
                $"Resource '{Name}': {string.Join(", ", notRunning.Select(c => $"{c.Service} is '{c.State}'"))}.");
        }

        if (containers.Any(c => c.Health == "healthy"))
        {
            ReadinessSource = "docker healthcheck";
            return;
        }

        if (ReadyWhenListeningOn is { } port)
        {
            await waitForPort(port, token);
            ReadinessSource = $"tcp connect to {port}";
            return;
        }

        // Weakest tier, and named as such: "running" only means the entrypoint has not exited.
        // A database accepting logins is a different claim, and nothing here has tested it.
        ReadinessSource = "container running (no healthcheck declared, no port or probe given)";
        Log?.Invoke(
            $"Resource '{Name}' is only known to be running — declare a healthcheck in the compose " +
            "file, or set ReadyWhenListeningOn/Probe, to actually establish readiness.");
    }

    /// <summary>No-op: containers are not reset between scenarios, they are recycled or left alone.</summary>
    public Task ResetBetweenScenarios() => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (!StopOnDispose) return;

        Log?.Invoke($"docker compose down ({Name})");
        try
        {
            await compose(["down"], TimeSpan.FromMinutes(2), CancellationToken.None);
        }
        catch (Exception e)
        {
            // Teardown failing must not mask the run's own result.
            Log?.Invoke($"Resource '{Name}': docker compose down failed — {e.Message}");
        }
    }

    // ── docker plumbing ─────────────────────────────────────────────────────

    /// <summary>One container as <c>docker compose ps</c> describes it.</summary>
    internal sealed record ContainerStatus(string Service, string State, string Health);

    /// <summary>
    /// Parses <c>docker compose ps --format json</c>, which emits one JSON object per line in
    /// current versions and a single array in older ones. Both are accepted.
    /// </summary>
    internal static IReadOnlyList<ContainerStatus> ParseStatus(string output)
    {
        var results = new List<ContainerStatus>();
        var trimmed = output.Trim();
        if (trimmed.Length == 0) return results;

        if (trimmed.StartsWith('['))
        {
            using var array = JsonDocument.Parse(trimmed);
            foreach (var element in array.RootElement.EnumerateArray()) results.Add(read(element));
            return results;
        }

        foreach (var line in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            using var document = JsonDocument.Parse(line);
            results.Add(read(document.RootElement));
        }

        return results;

        static ContainerStatus read(JsonElement element) => new(
            element.TryGetProperty("Service", out var s) ? s.GetString() ?? "" : "",
            element.TryGetProperty("State", out var st) ? st.GetString() ?? "" : "",
            // Empty means the service declares no healthcheck — NOT that it is unhealthy.
            element.TryGetProperty("Health", out var h) ? h.GetString() ?? "" : "");
    }

    private async Task<IReadOnlyList<ContainerStatus>> inspect(CancellationToken token)
    {
        var output = await compose(["ps", "--format", "json", .. Services], TimeSpan.FromSeconds(30), token);
        return ParseStatus(output);
    }

    private async Task waitForPort(int port, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + StartTimeout;

        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("localhost", port, token);
                return;
            }
            catch (Exception) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(500, token);
            }
        }
    }

    /// <summary>
    /// The full argument list, exposed so it can be asserted without running docker. Note the
    /// order: <c>-f</c> belongs to the <c>compose</c> subcommand, not to <c>docker</c> itself.
    /// </summary>
    internal IReadOnlyList<string> ArgumentsFor(IEnumerable<string> command)
        => ["compose", .. _composeFiles.SelectMany(f => new[] { "-f", f }), .. command];

    private async Task<string> compose(IEnumerable<string> command, TimeSpan timeout, CancellationToken token)
    {
        var info = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = WorkingDirectory ?? Directory.GetCurrentDirectory()
        };

        // "docker -f x compose up" is wrong; the -f belongs to the compose subcommand.
        info.ArgumentList.Add("compose");
        foreach (var file in _composeFiles)
        {
            info.ArgumentList.Add("-f");
            info.ArgumentList.Add(file);
        }

        foreach (var argument in command) info.ArgumentList.Add(argument);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Resource '{Name}': could not start the 'docker' process.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var reading = Task.WhenAll(
            drain(process.StandardOutput, stdout),
            drain(process.StandardError, stderr));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            tryKill(process);
            throw new TimeoutException(
                $"Resource '{Name}': 'docker compose {string.Join(' ', command)}' did not finish within " +
                $"{timeout.TotalSeconds:N0}s.{tail(stderr)}");
        }

        await reading;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Resource '{Name}': 'docker compose {string.Join(' ', command)}' exited with " +
                $"{process.ExitCode}.{tail(stderr)}");
        }

        return stdout.ToString();

        static async Task drain(StreamReader reader, StringBuilder into)
        {
            while (await reader.ReadLineAsync() is { } line) into.AppendLine(line);
        }

        static string tail(StringBuilder stderr)
        {
            var lines = stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0
                ? " docker produced no diagnostics — is the daemon running?"
                : Environment.NewLine + string.Join(Environment.NewLine, lines.TakeLast(10));
        }

        static void tryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone; nothing useful to do.
            }
        }
    }
}
