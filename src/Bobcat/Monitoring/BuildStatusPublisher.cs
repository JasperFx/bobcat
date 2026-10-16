using System.Net.Http.Json;
using System.Text.Json;

namespace Bobcat.Monitoring;

/// <summary>
/// Whether a watched spec project builds (bobcat#651): what <c>bobcat watch</c> reports as it
/// rebuilds, so a console can say a project no longer compiles instead of going on showing the last
/// run of code that is not there any more as green.
/// </summary>
/// <param name="Suite">The spec project — the same name its runs report as their suite.</param>
/// <param name="Repository">The checkout it is in, as a run's <c>run_started</c> names it.</param>
/// <param name="State"><see cref="Building"/>, <see cref="BuildFailed"/> or <see cref="Built"/>.</param>
/// <param name="Detail">The first compiler errors of a failed build; null otherwise.</param>
/// <remarks>
/// <para>
/// <b>Not an event on the ingest stream.</b> A build belongs to no run, and every event there names
/// one: a run-less event would mint a phantom run on every receiver that folds by run id, Stoat's
/// included. So it is its own resource, <c>POST /api/build-status</c>, beside <c>PUT
/// /api/event-model</c> — a receiver that does not serve it answers 404, which costs nothing.
/// </para>
/// <para>
/// The same words <c>stoat runner</c> reports on the runner wire for a resident runner it rebuilds
/// (<c>rebuilding</c> is its word for building), so the console reads both the same way.
/// </para>
/// </remarks>
public sealed record BuildStatusReport(
    string Suite,
    string? Repository,
    string? Branch,
    string State,
    string? Detail,
    DateTimeOffset At)
{
    public const string Building = "building";
    public const string BuildFailed = "build-failed";
    public const string Built = "built";

    /// <summary>Where a report is posted on a console.</summary>
    public const string Route = "/api/build-status";
}

/// <summary>
/// Posts a <see cref="BuildStatusReport"/> to every console <c>BOBCAT_MONITOR_URL</c> names
/// (bobcat#651), on the publisher's terms: a console that is absent, slow or older than the
/// endpoint never matters, and <c>BOBCAT_MONITOR=0</c> sends nothing.
/// </summary>
public static class BuildStatusPublisher
{
    /// <summary>How long one console has to take a report. A build loop must not wait on a monitor.</summary>
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How many compiler error lines a failed build reports.</summary>
    public const int ErrorLines = 10;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The report for <paramref name="suite"/>, built from the checkout <paramref name="directory"/> is in.
    /// </summary>
    public static BuildStatusReport Report(string suite, string directory, string state, string? detail = null)
    {
        var (repository, branch) = GitInfo.Discover(directory);
        return new BuildStatusReport(suite, repository ?? directory, branch, state, detail, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The compiler errors in a build's output, at most <see cref="ErrorLines"/> of them, deduplicated
    /// (a multi-targeted build reports each one per framework). Falls back to the output's last lines
    /// when no line reads as an error, so a failed build never reports nothing.
    /// </summary>
    public static string ErrorsIn(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var errors = lines
            .Where(line => line.Contains(": error ", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Take(ErrorLines)
            .ToList();

        return string.Join(Environment.NewLine, errors.Count > 0 ? errors : lines.TakeLast(ErrorLines));
    }

    /// <summary>Post <paramref name="report"/> to every console; never throws, and returns how many took it.</summary>
    public static async Task<int> PublishAsync(BuildStatusReport report, IReadOnlyList<string>? urls = null,
        HttpMessageHandler? handler = null)
    {
        if (MonitorPublisher.Disabled) return 0;

        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout;

        var results = await Task.WhenAll((urls ?? MonitorPublisher.ResolveUrls()).Select(async url =>
        {
            try
            {
                using var response = await client.PostAsJsonAsync(url.TrimEnd('/') + BuildStatusReport.Route, report, _json);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                // Absent, slow or hostile: the build loop goes on either way.
                return false;
            }
        }));

        return results.Count(x => x);
    }
}
