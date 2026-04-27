using Bobcat.Runtime;

namespace CqrsMinimalApi.Tests;

/// <summary>
/// Bobcat spec-runner entry point. Implemented as an explicit Main class
/// rather than top-level statements because the Tests project also
/// project-references the host project (CqrsMinimalApi), which itself uses
/// top-level statements and synthesizes a `Program` class in the global
/// namespace. Two `Program` classes in the same assembly cause
/// `AlbaResource&lt;Program&gt;` to bind to the wrong one (the empty
/// test-runner stub instead of the actual web-app entry point), which
/// surfaces as a hard PAL native crash on `AlbaHost.For&lt;Program&gt;()`
/// with no managed stack.
/// </summary>
public static class SpecsRunner
{
    public static Task<int> Main(string[] args)
    {
        // Capture unhandled exceptions on background threads so they print
        // a managed stack instead of being swallowed by the runtime abort.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine($"[Unhandled] {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Console.Error.WriteLine($"[UnobservedTask] {e.Exception}");
        };

        return BobcatRunner.Run(args, runner =>
        {
            // Use the global-namespace Program from the host project
            // (CqrsMinimalApi.csproj's top-level statements). With the
            // explicit Main above, this assembly no longer synthesizes a
            // competing `Program`, so unqualified Program here resolves
            // unambiguously to the host's entry point.
            runner.Suite.AddResource(new AlbaResource<Program>());
            runner.ScanForFeatures(typeof(CqrsMinimalApiFixture).Assembly);
        });
    }
}
