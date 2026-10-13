using Bobcat.Ui;
using Bobcat.Ui.Hosting;
using JasperFx.CommandLine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bobcat.Console;

public class UiInput
{
    [Description("Port to listen on; defaults to 5525, the address every Bobcat publisher probes")]
    [FlagAlias("port", 'p')]
    public int PortFlag { get; set; } = BobcatUiHost.DefaultPort;

    [Description("Do not open a browser")]
    public bool NoBrowserFlag { get; set; }

    [Description("Stop after a stretch with nothing connected and nothing publishing (Monitor:IdleMinutes, default two hours). For a console nobody is watching")]
    public bool IdleShutdownFlag { get; set; }
}

/// <summary>
/// bobcat#524 — <c>bobcat ui</c>: the run board and the Event Model canvas, served until Ctrl+C.
/// </summary>
/// <remarks>
/// <para>
/// Off unless somebody runs it. Every publisher probes 5525 once at startup and skips publishing,
/// for the whole run, when nothing answers — so a console that is not running costs a test run one
/// failed HTTP call and nothing else.
/// </para>
/// <para>
/// What it keeps while it runs is what the console always kept: one NDJSON archive per run and
/// one Event Model document per source, under <c>~/.bobcat/monitor/runs</c>
/// (<c>BOBCAT_MONITOR_DATA</c> moves it). A restarted console replays them.
/// </para>
/// </remarks>
[Description("Open the Bobcat UI: live test runs and the Event Model, in the browser", Name = "ui")]
public class UiCommand : JasperFxAsyncCommand<UiInput>
{
    public UiCommand()
    {
        Usage("Serve the Bobcat UI on 5525 and open it in the browser");
    }

    public override async Task<bool> Execute(UiInput input)
    {
        var url = BobcatUiHost.UrlFor(input.PortFlag);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // The tool's own directory: there is no appsettings to find, and the current directory
            // is wherever the person happened to be.
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.WebHost.UseUrls(url);

        // A terminal is the UI's log: the address and the stop key, then only what goes wrong.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        BobcatUiHost.Configure(builder, idleShutdown: input.IdleShutdownFlag);

        await using var app = BobcatUiHost.Map(builder.Build());

        try
        {
            await app.StartAsync();
        }
        catch (IOException e)
        {
            System.Console.Error.WriteLine(
                $"Could not listen on {url}: {e.Message}. Something else holds the port — another `bobcat ui`, or a Stoat host. " +
                "Stop it, or pass --port.");
            return false;
        }

        System.Console.WriteLine($"Bobcat UI on {url} — the run board at {BobcatUiHost.HomePageFor(url)}, the Event Model at {BobcatUiHost.EventModelPageFor(url)}");
        System.Console.WriteLine("Ctrl+C to stop.");

        if (!input.NoBrowserFlag && !Browser.Open(BobcatUiHost.HomePageFor(url)))
        {
            System.Console.WriteLine($"Could not open a browser; go to {BobcatUiHost.HomePageFor(url)}");
        }

        await app.WaitForShutdownAsync();
        return true;
    }
}
