using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bobcat.Alba.SampleWeb;

/// <summary>
/// Entry point of the sample host Bobcat.Alba.Tests boots through <c>AlbaResource&lt;Program&gt;</c>.
/// An explicit class rather than top-level statements, so the test project that references this
/// one never sees two global-namespace <c>Program</c> types (docs/sample-wiring.md footgun 1).
/// </summary>
public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<Counter>();

        var app = builder.Build();

        app.MapGet("/hello", () => "Hello from SampleWeb");
        app.MapGet("/content-root", (IWebHostEnvironment env) => env.ContentRootPath);
        // A singleton counter: its value survives requests but not a restart of the host, which is
        // what lets a test tell "the same host" from "a fresh one".
        app.MapGet("/counter/next", (Counter counter) => counter.Next());
        // Logs one line at each level with a caller-supplied marker, so a test can look for exactly
        // its own lines on the console and nobody else's.
        app.MapGet("/log/{marker}", (string marker, ILogger<Program> logger) =>
        {
            logger.LogInformation("SampleWeb info {Marker}", marker);
            logger.LogWarning("SampleWeb warning {Marker}", marker);
            return Results.Ok();
        });

        app.Run();
    }
}

public sealed class Counter
{
    private int _value;
    public int Next() => Interlocked.Increment(ref _value);
}
