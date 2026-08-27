using System.Diagnostics;
using JasperFx.CommandLine;

namespace Bobcat.Console.EventModel;

public class WatchEventModelInput
{
    [Description("The application whose Event Model to watch — a .csproj, or a directory containing one")]
    public string ProjectPath { get; set; } = ".";

    [Description("Base URL of the Bobcat console; defaults to http://localhost:5525")]
    [FlagAlias("url", 'u')]
    public string? UrlFlag { get; set; }

    [Description("Never start a console, even if nothing is answering — fail instead")]
    public bool NoStartFlag { get; set; }
}

/// <summary>
/// Issue #171 — <c>bobcat watch-event-model &lt;project&gt;</c>: one command that owns the whole
/// design-time loop. Starts a console if one is not already up, spawns the watcher against the
/// target application, and prints the page to open. Editing a handler redraws the diagram with no
/// further input.
/// </summary>
/// <remarks>
/// <para>
/// The three pieces existed and the incantation that joined them was one nobody would remember:
/// <c>dotnet watch run --project ../MyApp -- event-model --url http://localhost:5525/api/event-model</c>.
/// Wolverine's <c>--url</c> is wolverine#4146; the SignalR push that makes the page redraw is #169;
/// this is the name for the loop.
/// </para>
/// <para>
/// ⚠️ <b>It SPAWNS the rebuild.</b> A watch loop inside one process re-serializes the assembly it
/// already loaded, forever, and would never show an edit — which is why Wolverine's
/// <c>event-model</c> has no <c>--watch</c> of its own (design decision D1). Only a fresh process
/// picks up recompiled handlers, so this is always <c>dotnet watch run</c> around the export.
/// </para>
/// <para>
/// ⚠️ <b>The <c>--url</c> passed to the exporter is the full <c>/api/event-model</c> endpoint, not
/// the console's base URL.</b> Wolverine PUTs to that URL verbatim, so the base-URL form every
/// document currently shows — including Wolverine's own XML comment and this issue — answers 404.
/// Verified against a running console: base URL 404, endpoint 204. <see cref="EventModelWatchPlan"/>
/// composes it, so a caller passes the console's address and gets the working thing.
/// </para>
/// <para>
/// Every decision lives in <see cref="EventModelWatchPlan"/> and is unit-tested; what remains here
/// is process orchestration, which is not meaningfully unit-testable and is deliberately kept thin.
/// </para>
/// </remarks>
[Description("Watch an application's Event Model and keep a Bobcat console's diagram live as you edit", Name = "watch-event-model")]
public class WatchEventModelCommand : JasperFxAsyncCommand<WatchEventModelInput>
{
    /// <summary>How long to wait for a console this command started to begin answering.</summary>
    internal static TimeSpan ConsoleStartTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public WatchEventModelCommand()
    {
        Usage("Watch the application in the current directory").Arguments();
        Usage("Watch a named application").Arguments(x => x.ProjectPath);
    }

    public override async Task<bool> Execute(WatchEventModelInput input)
    {
        var url = string.IsNullOrWhiteSpace(input.UrlFlag)
            ? EventModelWatchPlan.DefaultConsoleUrl
            : input.UrlFlag;

        var probe = EventModelWatchPlan.For(input.ProjectPath, url, consoleIsUp: false);
        var consoleIsUp = await RespondsAsync(probe.EventModelApiUrl);

        var plan = EventModelWatchPlan.For(input.ProjectPath, url, consoleIsUp);

        if (plan.StartConsole)
        {
            if (input.NoStartFlag)
            {
                System.Console.Error.WriteLine(
                    $"Nothing is answering at {plan.ConsoleUrl} and --no-start was given. Start a console, or drop the flag.");
                return false;
            }

            if (!await startConsoleAsync(plan))
            {
                return false;
            }
        }
        else
        {
            System.Console.WriteLine($"Using the console already running at {plan.ConsoleUrl}.");
        }

        System.Console.WriteLine($"Event Model: {plan.EventModelPageUrl}");
        System.Console.WriteLine($"Watching {input.ProjectPath} — edit a handler and the diagram redraws. Ctrl-C to stop.");

        using var watcher = start("dotnet", plan.WatcherArguments);
        if (watcher is null)
        {
            System.Console.Error.WriteLine("Could not start `dotnet watch`. Is the .NET SDK on the PATH?");
            return false;
        }

        await watcher.WaitForExitAsync();
        return watcher.ExitCode == 0;
    }

    /// <summary>
    /// Does anything answer at <paramref name="apiUrl"/>? A 404 counts: a console that has never had
    /// a model published answers exactly that, and it is still very much up. Only a transport-level
    /// failure means "nothing is there".
    /// </summary>
    internal static async Task<bool> RespondsAsync(string apiUrl, TimeSpan? timeout = null)
    {
        using var client = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await client.GetAsync(apiUrl);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Start a console as a child process and wait for it to answer. `bobcat` with no arguments IS
    /// the console, so this re-invokes THIS executable rather than shelling out to `dotnet run`
    /// against a source tree the user may not have.
    /// </summary>
    private static async Task<bool> startConsoleAsync(EventModelWatchPlan plan)
    {
        var self = Environment.ProcessPath;
        if (self is null)
        {
            System.Console.Error.WriteLine(
                $"Nothing is answering at {plan.ConsoleUrl} and this command could not locate its own executable to start one.");
            return false;
        }

        System.Console.WriteLine($"Nothing at {plan.ConsoleUrl}; starting a console.");
        if (start(self, []) is null)
        {
            System.Console.Error.WriteLine("Could not start the console process.");
            return false;
        }

        var deadline = DateTimeOffset.UtcNow + ConsoleStartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await RespondsAsync(plan.EventModelApiUrl)) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        System.Console.Error.WriteLine(
            $"The console did not answer at {plan.ConsoleUrl} within {ConsoleStartTimeout.TotalSeconds:0} seconds.");
        return false;
    }

    /// <summary>
    /// Child processes inherit stdout/stderr deliberately: `dotnet watch`'s output IS the feedback
    /// that a rebuild happened, and swallowing it would make the loop silent.
    /// </summary>
    private static Process? start(string fileName, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            return Process.Start(info);
        }
        catch (Exception e)
        {
            System.Console.Error.WriteLine($"Could not start {fileName}: {e.Message}");
            return null;
        }
    }
}
