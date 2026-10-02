using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

// The one definition of the build. tests.yml runs `./build.sh CI` and publish.yml runs
// `./build.sh CI Pack`, so "the build is green" means the same thing on a laptop as on a push.
// Change what CI does here, not in the workflows.
partial class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Test);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution Solution;

    // Not artifacts/ itself: artifacts/local-feed is a hand-maintained NuGet feed that
    // samples/BankAccountES and the Wolverine CI branch consume, and Clean must never touch it.
    AbsolutePath PackagesDirectory => RootDirectory / "artifacts" / "packages";

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() => PackagesDirectory.CreateOrCleanDirectory());

    Target Restore => _ => _
        .Executes(() => DotNetRestore(s => s.SetProjectFile(Solution)));

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() => DotNetBuild(s => s
            .SetProjectFile(Solution)
            .SetConfiguration(Configuration)
            .EnableNoRestore()));

    // The database-backed integration tests skip locally without Postgres (saying so)
    // and fail on CI without them. Not a dependency of Test, deliberately: someone pointing
    // BOBCAT_POSTGRES at their own database should not have Docker started on their behalf.
    Target Docker => _ => _
        .Executes(() => ProcessTasks
            .StartProcess("docker", "compose --progress quiet up -d --wait", RootDirectory)
            .AssertZeroExitCode());

    Target Test => _ => _
        .DependsOn(Compile)
        .After(Docker)
        .Executes(() =>
        {
            try
            {
                DotNetTest(s => s
                    .SetProjectFile(Solution)
                    .SetConfiguration(Configuration)
                    .EnableNoBuild()
                    .EnableNoRestore());
            }
            catch
            {
                // A failed run through the solution prints only a per-assembly count — "Failed: 1,
                // Passed: 56" — and nothing about WHICH test or why. On a laptop that is fine,
                // because the run is repeatable; on CI it is the whole diagnosis, and chasing a
                // failure that only happens there meant instrumenting a test and pushing a tag to
                // find out. Microsoft.Testing.Platform writes the detail to a log beside each
                // assembly, so print it rather than leave a reader to guess.
                reportFailedTestLogs();
                throw;
            }
        });

    /// <summary>
    /// Prints the tail of every MTP test log that recorded a failure. Best effort by design: this
    /// runs while a build is already failing, and a problem reading a log must not replace the
    /// real failure with its own.
    /// </summary>
    private void reportFailedTestLogs()
    {
        try
        {
            // This configuration's logs only. The other one's are left over from an earlier run and
            // reporting them would name tests that are not failing now.
            var logs = (RootDirectory / "src")
                .GlobFiles($"**/bin/{Configuration}/**/TestResults/*.log")
                .ToList();

            foreach (var log in logs)
            {
                var lines = File.ReadAllLines(log);

                // The summary count, not the word: every log says "failed: 0" when it passed.
                if (!lines.Any(line => Regex.IsMatch(line, @"failed:\s*[1-9]"))) continue;

                // From the first failing test's own line, which is where the message and the stack
                // are. Capped, because a wedged suite can log a great deal after it.
                var first = Array.FindIndex(
                    lines, line => Regex.IsMatch(line, @"^\s*failed\s+\S"));

                Log.Error("───── {Log} ─────", log);
                foreach (var line in lines.Skip(Math.Max(0, first)).Take(150)) Log.Error("{Line}", line);
            }
        }
        catch (Exception e)
        {
            Log.Warning("Could not read the test logs: {Message}", e.Message);
        }
    }

    // Bobcat specs running THROUGH `dotnet test` is a supported path that the root run covers
    // only implicitly. Same guard as tests.yml: a run that collected zero tests is a failure,
    // because `dotnet test` does not reliably treat it as one.
    Target SpecsThroughDotnetTest => _ => _
        .DependsOn(Compile)
        .After(Docker)
        .After(Test)
        .Executes(() =>
        {
            var output = DotNetTest(s => s
                .SetProjectFile(RootDirectory / "src" / "Bobcat.Mtp.GeneratedHost")
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());

            var collected = output.Any(o => Regex.IsMatch(o.Text, @"Total: [1-9][0-9]*"));
            Assert.True(collected, "Bobcat.Mtp.GeneratedHost ran through dotnet test but collected no tests");
        });

    // What tests.yml runs on every push.
    Target CI => _ => _
        .DependsOn(Test, SpecsThroughDotnetTest);

    // What publish.yml packs: every packable project in the solution, plus the bobcat tool.
    Target Pack => _ => _
        .DependsOn(Clean, Compile)
        .Executes(() =>
        {
            DotNetPack(s => s
                .SetProject(Solution)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetOutputDirectory(PackagesDirectory));

            DotNetPack(s => s
                .SetProject(RootDirectory / "src" / "Bobcat.Console" / "Bobcat.Console.csproj")
                .SetConfiguration(Configuration)
                .SetOutputDirectory(PackagesDirectory));
        });
}
