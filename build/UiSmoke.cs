using System;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

// What ui-smoke.yml runs, as `./build.sh UiSmoke` (bobcat#524).
//
// Packs the bobcat tool the way the Pack target does — EmbedFrontend=true, which runs npm ci and
// the Vite build inside the pack — installs that package into a tool path, and hands the installed
// executable to build/ui-smoke/smoke.sh, which starts `bobcat ui` and checks it from the outside:
// the embedded page and its assets, deep links, the publisher wire, the CLI queries, a second UI on
// the same port, and a Chromium pass over the run board and the canvas. The reasoning for each
// check lives in that script.
//
// Needs Node and python3 (for a free port); the browser leg downloads Chromium on first use.
// BOBCAT_UI_SMOKE_SKIP_BROWSER=1 skips that leg.
partial class Build
{
    AbsolutePath UiSmokeDirectory => RootDirectory / "artifacts" / "ui-smoke";

    Target UiSmoke => _ => _
        .Executes(() =>
        {
            UiSmokeDirectory.CreateOrCleanDirectory();
            var packages = UiSmokeDirectory / "packages";
            var toolPath = UiSmokeDirectory / "tool";

            // A version nothing else uses: `dotnet tool install` resolves through the NuGet cache by
            // id and version, so re-installing the release version locally could hand back a
            // previously cached package instead of the one packed a second ago.
            var version = $"0.0.0-uismoke.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

            DotNetPack(s => s
                .SetProject(RootDirectory / "src" / "Bobcat.Console" / "Bobcat.Console.csproj")
                .SetConfiguration(Configuration)
                .SetProperty("EmbedFrontend", "true")
                .SetVersion(version)
                .SetOutputDirectory(packages));

            DotNetToolInstall(s => s
                .SetPackageName("Bobcat.Console")
                .SetVersion(version)
                .SetToolInstallationPath(toolPath)
                .AddSources(packages));

            var bobcat = toolPath / (OperatingSystem.IsWindows() ? "bobcat.exe" : "bobcat");
            ProcessTasks
                .StartProcess("bash", $"\"{RootDirectory / "build" / "ui-smoke" / "smoke.sh"}\" \"{bobcat}\"", RootDirectory)
                .AssertZeroExitCode();
        });
}
