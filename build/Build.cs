using System;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
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

    // Not artifacts/ itself: artifacts/local-feed is a hand-maintained NuGet feed that the
    // Wolverine repo's samples (moved there in #744) and its CI branch consume, and Clean must
    // never touch it.
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
            // `--solution`, not the positional path: global.json opts `dotnet test` into its
            // Microsoft.Testing.Platform mode (MTP 2.x refuses the VSTest bridge on the .NET 10
            // SDK), and that mode rejects a positional solution or project. It also prints each
            // failing test's name, message and stack to the console itself, so there is no
            // TestResults/*.log to go and read any more.
            //
            // The TUnit-hosted tests are back in (bobcat#521): they were taken out for 0.60.3 after
            // failing the publish job deterministically while passing tests.yml, and the one
            // difference the issue named was Nuke running Pack BETWEEN Compile and Test — Pack's
            // second `dotnet pack` has no --no-build, so it rebuilt core, the supervisor and the
            // tool just before the tests ran. Pack is now ordered After(Test), so `CI Pack` runs
            // exactly what tests.yml runs and then packs. BOBCAT_SKIP_TUNIT_HOSTED=1 still skips
            // them for anyone who needs it (and the TUnitTests target still runs only them).
            DotNetTest(s => s
                .SetProcessAdditionalArguments("--solution", Solution)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore());
        });

    const string TUnitHostedVariable = "BOBCAT_SKIP_TUNIT_HOSTED";

    // Only the TUnit-hosted tests, on demand: `./build.sh TUnitTests`. They are part of Test again
    // (bobcat#521); this stays because running the two child-host suites alone, without the other
    // two thousand tests, is how they get debugged.
    Target TUnitTests => _ => _
        .DependsOn(Compile)
        .After(Docker)
        .Executes(() =>
        {
            DotNetTest(s => s
                .SetProcessAdditionalArguments(
                    "--project", RootDirectory / "src" / "Bobcat.Supervisor.Tests" / "Bobcat.Supervisor.Tests.csproj",
                    "--filter-class", "Bobcat.Supervisor.Tests.TUnitResidentSuiteTests",
                    "--filter-class", "Bobcat.Supervisor.Tests.TUnitTreeNodeFilterTests")
                .SetConfiguration(Configuration)
                .SetProcessEnvironmentVariable(TUnitHostedVariable, "0")
                .EnableNoBuild()
                .EnableNoRestore());

            DotNetTest(s => s
                .SetProcessAdditionalArguments(
                    "--project", RootDirectory / "src" / "Bobcat.TUnit.Tests" / "Bobcat.TUnit.Tests.csproj")
                .SetConfiguration(Configuration)
                .SetProcessEnvironmentVariable(TUnitHostedVariable, "0")
                .EnableNoBuild()
                .EnableNoRestore());
        });

    // What tests.yml runs on every push. ResultsSchemaCompatibility (build/Contracts.cs) checks the
    // generated results schema against the last released package's (bobcat#604).
    Target CI => _ => _
        .DependsOn(Test, ResultsSchemaCompatibility);

    // What publish.yml packs: every packable project in the solution, plus the bobcat tool — which
    // only this second pack produces, because its IsPackable is gated on EmbedFrontend so it can
    // never ship without the Bobcat UI's SPA baked in (bobcat#524). EmbedFrontend runs npm ci and
    // the Vite build inside the pack, so the machine needs Node; publish.yml sets it up.
    //
    // After(Test), so `./build.sh CI Pack` runs Compile, Test, Pack — the order tests.yml already
    // proves, with the pack at the end. Nuke was free to put Pack in the middle, and then the
    // second `dotnet pack` below (no --no-build, deliberately: the tool's pack builds its own
    // publish output) rebuilt core, the supervisor and the tool between Compile and Test. A pack
    // must never change what the tests ran against; packing what has just been tested is also the
    // order a release wants.
    //
    // UiContracts and ResultsSchema (build/Contracts.cs) generate what is no longer committed
    // (bobcat#604): the SPA's TypeScript contracts, which EmbedFrontend's Vite build needs, and the
    // results schema Bobcat packs under schemas/.
    Target Pack => _ => _
        .DependsOn(Clean, Compile, UiContracts, ResultsSchema)
        .After(Test)
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
                .SetProperty("EmbedFrontend", "true")
                .SetOutputDirectory(PackagesDirectory));
        });
}
