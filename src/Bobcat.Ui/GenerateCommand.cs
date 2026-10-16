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
/// <c>relayRoutes.ts</c> (the event → runs-store routes). Both are gitignored (bobcat#604); the
/// Nuke <c>UiContracts</c> target and the frontend's npm pre-hooks run this. The work itself lives
/// in <see cref="TypeScriptContracts"/> so tests can call it without a process.
/// </summary>
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

        foreach (var (path, changed) in Write(frontEnd))
        {
            System.Console.WriteLine(changed ? $"Wrote {path}" : $"{path} was already current");
        }

        return true;
    }

    /// <summary>
    /// Write every generated file under <paramref name="frontEnd"/>/messages. A file whose contents
    /// are already right is left alone, so its timestamp does not wake a Vite dev server.
    /// </summary>
    public static IReadOnlyList<(string Path, bool Changed)> Write(string frontEnd)
    {
        var messages = Path.Combine(frontEnd, TypeScriptContracts.MessagesDirectory);
        Directory.CreateDirectory(messages);

        var results = new List<(string, bool)>();
        foreach (var (file, contents) in new[]
                 {
                     (TypeScriptContracts.MonitorEventsFile, TypeScriptContracts.GenerateMonitorEvents()),
                     (TypeScriptContracts.RelayRoutesFile, TypeScriptContracts.GenerateRelayRoutes())
                 })
        {
            var path = Path.Combine(messages, file);
            var changed = !File.Exists(path) || File.ReadAllText(path).Replace("\r\n", "\n") != contents;
            if (changed) File.WriteAllText(path, contents);
            results.Add((path, changed));
        }

        return results;
    }
}
