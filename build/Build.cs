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
            // `--solution`, not the positional path: global.json opts `dotnet test` into its
            // Microsoft.Testing.Platform mode (MTP 2.x refuses the VSTest bridge on the .NET 10
            // SDK), and that mode rejects a positional solution or project. It also prints each
            // failing test's name, message and stack to the console itself, so there is no
            // TestResults/*.log to go and read any more.
            //
            // BOBCAT_SKIP_TUNIT_HOSTED=1 TEMPORARILY takes the TUnit-hosted tests out of CI
            // (bobcat#521): they failed the 0.60.2 publish run deterministically while passing in
            // tests.yml. It skips TUnitResidentSuiteTests and TUnitTreeNodeFilterTests
            // ([TUnitHostedFact]/[TUnitHostedTheory]) and leaves Bobcat.TUnit.Tests out of the run
            // (its csproj). Run them with the TUnitTests target. Revert by removing this variable.
            DotNetTest(s => s
                .SetProcessAdditionalArguments("--solution", Solution)
                .SetConfiguration(Configuration)
                .SetProcessEnvironmentVariable(TUnitHostedVariable, "1")
                .EnableNoBuild()
                .EnableNoRestore());
        });

    const string TUnitHostedVariable = "BOBCAT_SKIP_TUNIT_HOSTED";

    // The tests CI leaves out for now (bobcat#521), on demand: `./build.sh TUnitTests`. Not part
    // of CI; delete it when they go back into Test.
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

    // What tests.yml runs on every push.
    Target CI => _ => _
        .DependsOn(Test);

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
