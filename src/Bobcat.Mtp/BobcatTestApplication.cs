using Bobcat.Runtime;
using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Capabilities.TestFramework;

namespace Bobcat.Mtp;

/// <summary>
/// Entry point for a Bobcat spec project that wants to be a test host.
/// </summary>
/// <example>
/// <code>
/// public static class SpecsRunner
/// {
///     public static Task&lt;int&gt; Main(string[] args)
///         =&gt; BobcatTestApplication.Run(args, runner =&gt;
///         {
///             runner.ScanForFeatures(typeof(SpecsRunner).Assembly);
///             runner.Suite.AddResource(new AlbaResource&lt;Program&gt;());
///         });
/// }
/// </code>
/// </example>
public static class BobcatTestApplication
{
    /// <summary>
    /// Builds and runs the test host. <paramref name="configure"/> is invoked once per platform
    /// request (discovery and execution are separate requests, and discovery deliberately never
    /// starts resources), so it must be safe to call more than once and must not itself do
    /// expensive work — register features and resources, nothing more.
    /// </summary>
    public static async Task<int> Run(string[] args, Action<BobcatRunner> configure)
    {
        var builder = await TestApplication.CreateBuilderAsync(args);

        // The MSBuild extension is what `dotnet test` talks to: it launches the host with
        // `--internal-msbuild-node <pipe>` and streams results back over it. The platform's
        // generated entry point would register this through a generated
        // AddSelfRegisteredExtensions — but that entry point is exactly what a Bobcat host turns
        // off to keep its own Main, so it has to be registered here. Without it the host answers
        // "Unknown option '--internal-msbuild-node'" and `dotnet test` reports the project failed
        // before a single scenario ran; running the executable directly never showed it, which is
        // how Bobcat.Monitor.Specs became the first Bobcat host `dotnet test` ever collected.
        Microsoft.Testing.Platform.MSBuild.TestingPlatformBuilderHook.AddExtensions(builder, args);

        builder.RegisterTestFramework(
            _ => new TestFrameworkCapabilities(),
            (capabilities, _) => new BobcatTestFramework(configure, capabilities));

        using var app = await builder.BuildAsync();
        return await app.RunAsync();
    }
}
