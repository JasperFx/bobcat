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

    // The Marten and RabbitMQ integration tests skip locally without these services (saying so)
    // and fail on CI without them. Not a dependency of Test, deliberately: someone pointing
    // BOBCAT_POSTGRES at their own database should not have Docker started on their behalf.
    Target Docker => _ => _
        .Executes(() => ProcessTasks
            .StartProcess("docker", "compose --progress quiet up -d --wait", RootDirectory)
            .AssertZeroExitCode());

    Target Test => _ => _
        .DependsOn(Compile)
        .After(Docker)
        .Executes(() => DotNetTest(s => s
            .SetProjectFile(Solution)
            .SetConfiguration(Configuration)
            .EnableNoBuild()
            .EnableNoRestore()));

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
