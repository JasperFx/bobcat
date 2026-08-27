namespace Bobcat.Console.EventModel;

/// <summary>
/// Issue #171 — everything <c>bobcat watch-event-model</c> is going to DO, decided from its input
/// and one fact about the world, before a single process is started.
/// </summary>
/// <remarks>
/// <para>
/// The command itself is pure process orchestration, which is the part a test cannot exercise
/// without a target project, a console and a browser. So the decisions live here, where they are
/// ordinary values: the CritterWatch <c>cw-*</c> precedent of extracting the decidable-from-input
/// half and testing that, rather than pretending an <c>Execute</c> over three child processes is
/// unit-testable.
/// </para>
/// </remarks>
/// <param name="ConsoleUrl">The console's base URL — where the watcher PUTs and where to open a browser.</param>
/// <param name="StartConsole">True when nothing answered at <see cref="ConsoleUrl"/> and one must be started.</param>
/// <param name="WatcherArguments">
///     The argument list for <c>dotnet</c>. Deliberately a list, not a string: a project path with a
///     space in it is the normal case on macOS and Windows, and quoting it back into one command
///     line is how that breaks.
/// </param>
public sealed record EventModelWatchPlan(
    string ConsoleUrl,
    bool StartConsole,
    IReadOnlyList<string> WatcherArguments)
{
    /// <summary>The console's default address — the same one <c>launchSettings.json</c> uses.</summary>
    public const string DefaultConsoleUrl = "http://localhost:5525";

    /// <summary>Where the SPA renders the model, for the "open this" line the command prints.</summary>
    public string EventModelPageUrl => $"{ConsoleUrl.TrimEnd('/')}/event-model";

    /// <summary>The endpoint the watcher PUTs to, and the one the liveness probe reads.</summary>
    public string EventModelApiUrl => $"{ConsoleUrl.TrimEnd('/')}/api/event-model";

    /// <summary>
    /// Compose the plan.
    /// </summary>
    /// <param name="projectPath">The target application's project or directory.</param>
    /// <param name="consoleUrl">An explicit console URL, or null for <see cref="DefaultConsoleUrl"/>.</param>
    /// <param name="consoleIsUp">Whether something already answered at the console URL.</param>
    /// <remarks>
    /// ⚠️ <b>The watcher must SPAWN the rebuild, not loop inside one process.</b> A watch loop in a
    /// single process re-serializes the assembly it already loaded, forever, and would never show an
    /// edit — that is exactly why Wolverine's <c>event-model</c> has no <c>--watch</c> flag of its
    /// own and grew a <c>--url</c> instead (wolverine#4146, design decisions D1/D2). So this is
    /// always <c>dotnet watch run</c> around the export, and never anything cleverer.
    /// </remarks>
    public static EventModelWatchPlan For(string projectPath, string? consoleUrl, bool consoleIsUp)
    {
        var url = string.IsNullOrWhiteSpace(consoleUrl) ? DefaultConsoleUrl : consoleUrl.Trim().TrimEnd('/');

        return new EventModelWatchPlan(
            url,
            StartConsole: !consoleIsUp,
            WatcherArguments:
            [
                "watch",
                "run",
                "--project", projectPath,
                // Everything after `--` goes to the application rather than to `dotnet watch`.
                "--",
                "event-model",
                "--url", $"{url}/api/event-model",
            ]);
    }
}
