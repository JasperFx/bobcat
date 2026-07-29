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

        builder.RegisterTestFramework(
            _ => new TestFrameworkCapabilities(),
            (capabilities, _) => new BobcatTestFramework(configure, capabilities));

        using var app = await builder.BuildAsync();
        return await app.RunAsync();
    }
}
