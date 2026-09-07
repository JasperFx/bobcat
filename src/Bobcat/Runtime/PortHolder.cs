using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Bobcat.Runtime;

/// <summary>
/// Names the process holding a TCP port when a resource fails to bind one (issue #200).
/// </summary>
/// <remarks>
/// An orphaned <c>bobcat run</c> sat on Kestrel's default port for nearly 21 hours; the next
/// repository's gate then failed 15 tests across two suites with <c>AddressInUseException</c>,
/// and the red read as a product regression until <c>lsof -iTCP:5000</c> named the squatter. The
/// answer was one shell call away from the failure message the entire time.
///
/// Report, never act. This says who holds the port and stops there: killing somebody else's
/// process because it is in our way is not a decision a test harness gets to make, and the
/// holder is as likely to be a development server somebody is using as it is to be an orphan.
/// </remarks>
public static partial class PortHolder
{
    /// <summary>How long the platform's socket-listing tool gets before we give up on it.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A sentence to append to a resource start-up failure, or empty when the failure was not a
    /// bind collision. Never throws — a diagnostic that fails must not replace the real error.
    /// </summary>
    public static string Explain(Exception exception)
    {
        try
        {
            if (PortOf(exception) is not { } port) return string.Empty;

            return Describe(port) is { } holder
                ? $"{Environment.NewLine}Port {port} is held by {holder} — that process, not this suite, is what has to go."
                : $"{Environment.NewLine}Port {port} is already in use, and Bobcat could not identify what holds it "
                  + $"(try `lsof -nP -iTCP:{port} -sTCP:LISTEN`).";
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The port a bind collision was about, or null when this exception chain is not one. The
    /// address lives in the wrapping message ("Failed to bind to address http://127.0.0.1:5000"),
    /// never on the socket error itself, so both halves have to be present.
    /// </summary>
    public static int? PortOf(Exception exception)
    {
        var collided = false;
        for (var walk = exception; walk is not null; walk = walk.InnerException)
        {
            // Kestrel's AddressInUseException lives in an ASP.NET assembly this project must not
            // reference, so it is matched by name; the SocketException it wraps is BCL and is
            // matched properly. Either one on its own is enough to call it a collision.
            collided |= walk is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                        || walk.GetType().Name == "AddressInUseException";
        }

        if (!collided) return null;

        for (var walk = exception; walk is not null; walk = walk.InnerException)
        {
            if (PortPattern().Match(walk.Message) is { Success: true } match
                && int.TryParse(match.Groups[1].Value, out var port))
            {
                return port;
            }
        }

        return null;
    }

    /// <summary>
    /// Who is listening on a port — <c>"pid 1234 (bobcat)"</c> — or null when nothing could be
    /// determined, which covers both "the holder released it" and "this machine has no tool to
    /// ask with". Deliberately not distinguished: a diagnostic that guesses is worse than one
    /// that says it does not know.
    /// </summary>
    public static string? Describe(int port)
        => OperatingSystem.IsWindows() ? fromNetstat(port) : fromLsof(port);

    private static string? fromLsof(int port)
    {
        // -n and -P keep it fast and literal: no DNS, no /etc/services translation.
        var output = run("lsof", $"-nP -iTCP:{port} -sTCP:LISTEN");
        if (output is null) return null;

        foreach (var line in output.Split('\n').Skip(1))
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 2 && int.TryParse(columns[1], out var pid))
            {
                return $"pid {pid} ({columns[0]})";
            }
        }

        return null;
    }

    private static string? fromNetstat(int port)
    {
        var output = run("netstat", "-ano");
        if (output is null) return null;

        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains($":{port} ") || !line.Contains("LISTENING")) continue;

            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!int.TryParse(columns[^1], out var pid)) continue;

            return $"pid {pid} ({nameOf(pid)})";
        }

        return null;
    }

    private static string nameOf(int pid)
    {
        try
        {
            return Process.GetProcessById(pid).ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    private static string? run(string command, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(Timeout) ? output : null;
        }
        catch
        {
            // No such tool, no permission, a sandbox that forbids spawning: all the same answer.
            return null;
        }
    }

    [GeneratedRegex(@":(\d{1,5})(?:\D|$)")]
    private static partial Regex PortPattern();
}
