using Bobcat.Mtp;
using Bobcat.Runtime;

namespace Bobcat.CodeFirst.Samples;

/// <summary>
/// The sample suite as a Microsoft.Testing.Platform host. Runnable directly
/// (<c>dotnet run</c>, <c>--list-tests</c>, <c>--filter-uid "Order saga/Starting an order"</c>) and
/// collected by <c>dotnet test</c> at the solution root.
/// </summary>
/// <remarks>
/// <c>dotnet run -- report</c> runs the same suite through <see cref="BobcatRunner.Run"/> instead,
/// which prints Bobcat's own step-by-step report — the thing the MTP host has to suppress because
/// the platform owns the console. That is the view <c>docs/code-first-specs.md</c> quotes.
/// </remarks>
public static class SpecsRunner
{
    public static Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "report")
            return BobcatRunner.Run(args[1..], Configure);

        return BobcatTestApplication.Run(args, Configure);
    }

    public static void Configure(BobcatRunner runner)
    {
        if (!Postgres.IsCi && !Postgres.IsAvailable)
        {
            // No scenarios, and the reason on stderr. The csproj ignores the platform's zero-tests
            // exit code so this is a green, explained no-op rather than a red build.
            Console.Error.WriteLine(Postgres.SkipReason);
            return;
        }

        runner.ScanForSpecifications(typeof(SpecsRunner).Assembly);
        runner.Suite.AddResource(Hosts.AppHost());
        runner.Suite.AddResource(Hosts.ProjectionsHost());
    }
}
