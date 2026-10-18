using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Bobcat.Supervisor;

/// <summary>
/// Something other than a worker whose memory the supervisor samples beside the workers'
/// (bobcat#702) — typically the database container. On a hosted runner Postgres or SQL Server
/// is often the larger consumer, and what kills a runner is the total, so worker peaks alone
/// are half the story.
/// </summary>
/// <remarks>
/// Sampled only when <see cref="Supervisor.ResourceSampleInterval"/> is set, at the same
/// cadence as the workers, plus once before the first test and once after the last. Report,
/// don't act — the same guardrail as the worker figures.
/// </remarks>
public interface IMemorySampleTarget
{
    /// <summary>What the report calls it — "postgres", "sqlserver".</summary>
    string Name { get; }

    /// <summary>
    /// The target's current memory in bytes, or null when it cannot be measured right now.
    /// Never zero for "unknown": a null is how the report tells "not measured" from "empty".
    /// May throw; a throw is read as null.
    /// </summary>
    Task<long?> SampleBytes(CancellationToken ct);
}

/// <summary>The shipped <see cref="IMemorySampleTarget"/>s.</summary>
public static class MemorySampleTargets
{
    /// <summary>
    /// A Docker container's memory as <c>docker stats --no-stream</c> reports it — the
    /// container's whole cgroup, every backend process included. Works wherever the
    /// <c>docker</c> CLI does, including Docker Desktop, where the container's processes are
    /// inside a VM and no host pid can see them.
    /// </summary>
    /// <param name="container">The container's name or id, as <c>docker stats</c> takes it.</param>
    /// <param name="name">What the report calls it; defaults to the container name.</param>
    public static IMemorySampleTarget Container(string container, string? name = null)
        => new DockerContainerMemory(container, name ?? container);

    /// <summary>
    /// One host process's resident set, by a pid read fresh on every sample — so a database
    /// recycled mid-run is followed to its new pid. Only that process's own working set, not
    /// its children: for a Postgres with a process per connection, <see cref="Container"/> is
    /// the honest total.
    /// </summary>
    public static IMemorySampleTarget Process(string name, Func<int?> processId)
        => new ProcessMemory(name, processId);
}

internal sealed class ProcessMemory(string name, Func<int?> processId) : IMemorySampleTarget
{
    public string Name { get; } = name;

    public Task<long?> SampleBytes(CancellationToken ct)
    {
        if (processId() is not { } pid) return Task.FromResult<long?>(null);

        using var process = System.Diagnostics.Process.GetProcessById(pid);
        return Task.FromResult<long?>(process.HasExited ? null : process.WorkingSet64);
    }
}

internal sealed partial class DockerContainerMemory(string container, string name) : IMemorySampleTarget
{
    public string Name { get; } = name;

    public string ContainerName { get; } = container;

    /// <summary><c>docker stats</c> takes a second or two; anything past this is unmeasured.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    public async Task<long?> SampleBytes(CancellationToken ct)
    {
        var info = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in Arguments) info.ArgumentList.Add(argument);

        using var process = System.Diagnostics.Process.Start(info);
        if (process is null) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cts.Token);
            _ = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            return process.ExitCode == 0 ? ParseMemUsage(await output) : null;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }

            return null;
        }
    }

    internal IReadOnlyList<string> Arguments
        => ["stats", "--no-stream", "--format", "{{.MemUsage}}", ContainerName];

    /// <summary>
    /// Reads the used half of <c>docker stats</c>' MemUsage column — <c>"1.234GiB / 7.66GiB"</c>.
    /// Docker writes binary units (<c>MiB</c>), but decimal ones (<c>MB</c>) are read too.
    /// Anything else is null, never zero.
    /// </summary>
    internal static long? ParseMemUsage(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (line is null) return null;

        var match = memUsage().Match(line);
        if (!match.Success) return null;

        if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var value))
        {
            return null;
        }

        double? multiplier = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "b" => 1,
            "kib" => 1024d,
            "mib" => 1024d * 1024,
            "gib" => 1024d * 1024 * 1024,
            "tib" => 1024d * 1024 * 1024 * 1024,
            "kb" => 1e3,
            "mb" => 1e6,
            "gb" => 1e9,
            "tb" => 1e12,
            _ => null
        };

        return multiplier is { } m ? (long)Math.Round(value * m) : null;
    }

    [GeneratedRegex(@"^\s*(?<value>\d+(\.\d+)?)\s*(?<unit>[KMGT]?i?B)\b", RegexOptions.IgnoreCase)]
    private static partial Regex memUsage();
}
