using Bobcat.Monitor.Contracts;
using JasperFx.CommandLine;

namespace Bobcat.Monitor;

public class GenerateInput
{
    [Description("Write nothing; exit non-zero if the committed TypeScript differs from what the C# contracts generate")]
    public bool CheckFlag { get; set; }

    [Description("The SPA's src directory; discovered by walking up to the repository root when omitted")]
    public string? FrontEndFlag { get; set; }
}

/// <summary>
/// <c>dotnet run --project src/Bobcat.Monitor -- generate</c> — regenerates the Vue SPA's
/// TypeScript contract mirrors from <c>Contracts/MonitorEvents.cs</c>. CritterWatch's
/// <c>GenerateCommand</c>, cut down; the work itself lives in <see cref="TypeScriptContracts"/>
/// so the drift test can call it without a process.
/// </summary>
[Description("Generate the SPA's TypeScript mirrors of the monitor event contracts (monitor-events.ts + relayToStore cases)")]
public class GenerateCommand : JasperFxCommand<GenerateInput>
{
    public override bool Execute(GenerateInput input)
    {
        var frontEnd = input.FrontEndFlag ?? TypeScriptContracts.FindFrontEndSourceDirectory();
        if (frontEnd is null)
        {
            Console.Error.WriteLine("Could not find src/Bobcat.Monitor.FrontEnd/src above the current directory; pass --front-end <path>.");
            return false;
        }

        var messages = Path.Combine(frontEnd, TypeScriptContracts.MessagesDirectory);
        var eventsPath = Path.Combine(messages, TypeScriptContracts.MonitorEventsFile);
        var relayPath = Path.Combine(messages, TypeScriptContracts.RelayToStoreFile);

        var events = TypeScriptContracts.GenerateMonitorEvents();
        var relay = TypeScriptContracts.PatchRelayToStore(File.ReadAllText(relayPath));

        var eventsChanged = !File.Exists(eventsPath) || normalize(File.ReadAllText(eventsPath)) != events;
        var relayChanged = normalize(File.ReadAllText(relayPath)) != relay;

        if (input.CheckFlag)
        {
            if (!eventsChanged && !relayChanged)
            {
                Console.WriteLine("TypeScript contract mirrors are current.");
                return true;
            }

            if (eventsChanged) Console.Error.WriteLine($"{eventsPath} differs from the generated output.");
            if (relayChanged) Console.Error.WriteLine($"{relayPath} is missing a generated case or import.");
            Console.Error.WriteLine($"Run `{TypeScriptContracts.GenerateCommandLine}` and commit the result.");
            return false;
        }

        if (eventsChanged)
        {
            File.WriteAllText(eventsPath, events);
            Console.WriteLine($"Wrote {eventsPath}");
        }

        if (relayChanged)
        {
            File.WriteAllText(relayPath, relay);
            Console.WriteLine($"Patched {relayPath}");
        }

        if (!eventsChanged && !relayChanged) Console.WriteLine("TypeScript contract mirrors were already current.");

        foreach (var (type, wireName) in TypeScriptContracts.EventTypes)
        {
            Console.WriteLine($"  {wireName,-24} {type.Name}");
        }

        return true;
    }

    private static string normalize(string text) => text.Replace("\r\n", "\n");
}
