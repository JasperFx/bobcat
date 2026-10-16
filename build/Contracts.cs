using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

// The generated contracts (bobcat#604). Neither is committed, because parallel PRs that touched the
// wire kept conflicting on them even when their C# merged cleanly:
//
//   - the SPA's TypeScript wire mirror, src/Bobcat.Ui.FrontEnd/src/messages/monitor-events.ts and
//     relayRoutes.ts (gitignored), from the C# records in src/Bobcat.Ui/Contracts — UiContracts;
//   - the results file's JSON Schema, from ResultsFile.GenerateSchema — ResultsSchema writes it to
//     artifacts/schemas for Pack, and ResultsSchemaCompatibility (part of CI) compares it with the
//     copy in the last released Bobcat package on nuget.org.
//
// Both run the bobcat tool's own commands (`generate-ui-contracts`, `results-schema`), so there is
// one generator and the build only decides when it runs.
partial class Build
{
    AbsolutePath ConsoleProject => RootDirectory / "src" / "Bobcat.Console" / "Bobcat.Console.csproj";
    AbsolutePath SchemasDirectory => RootDirectory / "artifacts" / "schemas";

    [Parameter("A results schema file to check against instead of the last released package's (ResultsSchemaCompatibility)")]
    readonly AbsolutePath SchemaBaseline;

    static string Quote(object value) => $"\"{value}\"";

    void RunBobcatTool(string arguments) => DotNet(
        $"run --project {ConsoleProject} --configuration {Configuration} -- {arguments:nq}",
        RootDirectory,
        environmentVariables: new System.Collections.Generic.Dictionary<string, string>(EnvironmentInfo.Variables) { ["BOBCAT_MONITOR"] = "0" });

    // What UiSmoke, Pack and ui-frontend.yml run before anything compiles the SPA. The frontend's npm
    // pre-hooks (scripts/ui-contracts.mjs) do the same for local work, from the built tool.
    Target UiContracts => _ => _
        .After(Compile)
        .Executes(() => RunBobcatTool(
            $"generate-ui-contracts --front-end {Quote((RootDirectory / "src" / "Bobcat.Ui.FrontEnd" / "src"))}"));

    // The schema Pack puts under schemas/ in the Bobcat package (src/Bobcat/Bobcat.csproj packs
    // artifacts/schemas, and refuses to pack without it).
    Target ResultsSchema => _ => _
        .After(Compile)
        .Executes(() =>
        {
            SchemasDirectory.CreateOrCleanDirectory();
            RunBobcatTool($"results-schema --out {Quote(SchemasDirectory)}");
        });

    // The review signal the committed schema used to give, against what consumers actually have:
    // the last released package's schema. Fails on a breaking change (a removed property, a changed
    // type, a newly required property) unless ResultsFile.SchemaVersion moved; additive changes pass.
    // A released package with no schema (everything before #540) is no baseline, and passes saying so.
    Target ResultsSchemaCompatibility => _ => _
        .After(Compile)
        .Executes(() =>
        {
            var baseline = SchemaBaseline;
            var description = SchemaBaseline is null ? null : $"{SchemaBaseline}";

            if (baseline is null)
            {
                (baseline, description) = DownloadReleasedSchema();
                if (baseline is null) return;
            }

            RunBobcatTool($"results-schema --baseline {Quote(baseline)} --baseline-name {Quote(description)}");
        });

    (AbsolutePath Schema, string Description) DownloadReleasedSchema()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        const string feed = "https://api.nuget.org/v3-flatcontainer/bobcat";

        var index = JsonDocument.Parse(http.GetStringAsync($"{feed}/index.json").GetAwaiter().GetResult());
        // Ascending SemVer order, so the last entry is the last release.
        var version = index.RootElement.GetProperty("versions").EnumerateArray().Last().GetString()!;

        var directory = TemporaryDirectory / "results-schema-baseline" / version;
        directory.CreateOrCleanDirectory();
        var package = directory / $"bobcat.{version}.nupkg";
        File.WriteAllBytes(package, http.GetByteArrayAsync($"{feed}/{version}/bobcat.{version}.nupkg").GetAwaiter().GetResult());

        using var zip = ZipFile.OpenRead(package);
        var schemas = zip.Entries
            .Where(e => e.FullName.StartsWith("schemas/", StringComparison.Ordinal)
                        && e.Name.StartsWith("bobcat-results-", StringComparison.Ordinal)
                        && e.Name.EndsWith(".schema.json", StringComparison.Ordinal))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        if (schemas.Count == 0)
        {
            Log.Information("Bobcat {Version}, the last release on nuget.org, carries no results schema: there is no released baseline, so there is nothing to be compatible with yet.", version);
            return (null, null);
        }

        var entry = schemas.Last();
        var schema = directory / entry.Name;
        entry.ExtractToFile(schema, overwrite: true);
        return (schema, $"Bobcat {version} on nuget.org");
    }
}
