using Bobcat.Ui.Contracts;
using JasperFx.CommandLine;

namespace Bobcat.Ui;

public class GenerateInput
{
    [Description("The SPA's src directory; discovered by walking up to the repository root when omitted")]
    public string? FrontEndFlag { get; set; }
}

/// <summary>
/// <c>bobcat generate-ui-contracts</c> — writes the Vue SPA's generated TypeScript from
/// <c>Contracts/MonitorEvents.cs</c>: <c>monitor-events.ts</c> (the interfaces) and
/// <c>relayRoutes.ts</c> (the event → runs-store routes), and <c>results-file.ts</c> (the results
/// file's shape, for the HTML results page, bobcat#541). All are gitignored (bobcat#604); the Nuke
/// <c>UiContracts</c> target and the frontend's npm pre-hooks run this. The work itself lives in
/// <see cref="TypeScriptContracts"/> so tests can call it without a process.
/// </summary>
/// <remarks>
/// The results file's schema lives in core, which this assembly does not reference, so it is
/// <see cref="ResultsSchema"/>: the tool registers a subclass that supplies it. Without one the
/// results types are not written, and the command says so rather than leaving a stale file.
/// </remarks>
[Description("Contributors: generate the Bobcat UI SPA's TypeScript mirrors of the monitor event contracts (./build.sh UiContracts)", Name = "generate-ui-contracts")]
public class GenerateCommand : JasperFxCommand<GenerateInput>
{
    public override bool Execute(GenerateInput input)
    {
        var frontEnd = input.FrontEndFlag ?? TypeScriptContracts.FindFrontEndSourceDirectory();
        if (frontEnd is null)
        {
            System.Console.Error.WriteLine("Could not find src/Bobcat.Ui.FrontEnd/src above the current directory; pass --front-end <path>.");
            return false;
        }

        if (ResultsSchema is null)
        {
            System.Console.Error.WriteLine($"No results file schema was supplied, so {TypeScriptContracts.ResultsFileTypesFile} is not written.");
        }

        foreach (var (path, changed) in Write(frontEnd, ResultsSchema))
        {
            System.Console.WriteLine(changed ? $"Wrote {path}" : $"{path} was already current");
        }

        return true;
    }

    /// <summary>The results file's JSON Schema, which results-file.ts is generated from; null here.</summary>
    protected virtual string? ResultsSchema => null;

    /// <summary>
    /// Write every generated file under <paramref name="frontEnd"/>/messages. A file whose contents
    /// are already right is left alone, so its timestamp does not wake a Vite dev server.
    /// results-file.ts is written only when <paramref name="resultsSchema"/> is given.
    /// </summary>
    public static IReadOnlyList<(string Path, bool Changed)> Write(string frontEnd, string? resultsSchema = null)
    {
        var messages = Path.Combine(frontEnd, TypeScriptContracts.MessagesDirectory);
        Directory.CreateDirectory(messages);

        var results = new List<(string, bool)>();
        var files = new List<(string File, string Contents)>
        {
            (TypeScriptContracts.MonitorEventsFile, TypeScriptContracts.GenerateMonitorEvents()),
            (TypeScriptContracts.RelayRoutesFile, TypeScriptContracts.GenerateRelayRoutes())
        };

        if (resultsSchema is not null)
        {
            files.Add((TypeScriptContracts.ResultsFileTypesFile, TypeScriptContracts.GenerateResultsFile(resultsSchema)));
        }

        foreach (var (file, contents) in files)
        {
            var path = Path.Combine(messages, file);
            var changed = !File.Exists(path) || File.ReadAllText(path).Replace("\r\n", "\n") != contents;
            if (changed) File.WriteAllText(path, contents);
            results.Add((path, changed));
        }

        return results;
    }
}
