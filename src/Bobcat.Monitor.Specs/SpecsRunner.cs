using Bobcat.Mtp;

namespace Bobcat.Monitor.Specs;

/// <summary>
/// The viewer's end-to-end suite as a Microsoft.Testing.Platform host. An explicit
/// <c>Main</c> rather than top-level statements, so this assembly never synthesizes a
/// <c>Program</c> of its own — <see cref="MonitorHost"/> bootstraps the viewer through the real
/// <c>Program</c> in Bobcat.Monitor, and two of them in one compilation is the PAL-crash footgun
/// docs/sample-wiring.md opens with.
/// </summary>
/// <remarks>
/// The suite's OWN progress publishes wherever any spec host's does — the default
/// <c>BOBCAT_MONITOR_URL</c> (localhost:5525) when a viewer is running there, and nowhere
/// otherwise. That is the dogfood loop closing: a developer with <c>dotnet bobcat</c> open sees
/// this suite run while it tests a second, in-memory viewer. There is no way for the two to
/// loop — the instance under test lives on Alba's TestServer and has no address to publish to.
/// CI sets <c>BOBCAT_MONITOR=0</c> so the probe never happens there at all.
/// </remarks>
public static class SpecsRunner
{
    public static Task<int> Main(string[] args)
        => BobcatTestApplication.Run(args, runner =>
        {
            runner.ScanForFeatures(typeof(SpecsRunner).Assembly);
            runner.Suite.AddResource(new MonitorHost());
        });
}
