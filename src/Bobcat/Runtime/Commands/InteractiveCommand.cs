using Bobcat.Rendering;
using JasperFx.CommandLine;
using Spectre.Console;

namespace Bobcat.Runtime.Commands;

/// <summary>
/// A REPL for specs (issue #209): Spectre selection prompts over the feature/scenario tree,
/// run-or-preview per selection, with the suite's resources held WARM between selections —
/// StartAll runs once, on the first run, and DisposeAsync on exit, so re-running a scenario
/// costs the scenario rather than a Postgres/host startup. Each selected run still gets the
/// full ResetAll → BeginScenarioAll → EndScenarioAll bracket per scenario.
/// </summary>
[Description("Interactively pick scenarios to run or preview, keeping resources warm between runs", Name = "interactive")]
public class InteractiveCommand : JasperFxAsyncCommand<BobcatInput>
{
    public override async Task<bool> Execute(BobcatInput input)
    {
        // TTY gating is mandatory: an interactive prompt on a redirected console is a hung
        // build, and Bobcat's whole supervisor story is about never hanging silently.
        var refusal = DescribeNonInteractiveConsole(
            Console.IsInputRedirected, Console.IsOutputRedirected,
            AnsiConsole.Profile.Capabilities.Interactive);
        if (refusal != null)
        {
            Console.Error.WriteLine(refusal);
            input.ExitCode = 1;
            return true;
        }

        var runner = input.Runner;
        var renderer = new CommandLineRenderer();

        var tree = runner.SelectFeatures(input.FeatureFlag)
            .Select(f => (Feature: f, Scenarios: runner.SelectScenarios(f, input.TagFlag).ToArray()))
            .Where(pair => pair.Scenarios.Length > 0)
            .ToList();

        if (tree.Count == 0)
        {
            Console.WriteLine("No features or scenarios match the filters.");
            input.ExitCode = 0;
            return true;
        }

        var choices = InteractiveChoices.Build(tree);

        // Resources start lazily on the first RUN — a preview-only session never starts any,
        // the same rule the preview command lives by.
        var warm = false;
        var exitCode = 0;

        try
        {
            while (true)
            {
                var choice = AnsiConsole.Prompt(new SelectionPrompt<InteractiveChoice>()
                    .Title("Select a feature or scenario")
                    .PageSize(20)
                    .MoreChoicesText("[dim](move up and down to see more)[/]")
                    .AddChoices(choices)
                    .UseConverter(c => c.Label));

                if (choice.IsExit) break;

                var action = AnsiConsole.Prompt(new SelectionPrompt<string>()
                    .Title($"{Markup.Escape(choice.Label.Trim())} — what now?")
                    .AddChoices("Run", "Preview", "Back"));

                if (action == "Back") continue;

                var feature = choice.Feature!;
                if (action == "Preview")
                {
                    renderer.RenderFeatureHeader(feature.Title);
                    foreach (var scenario in tree.First(pair => ReferenceEquals(pair.Feature, feature)).Scenarios)
                    {
                        if (choice.Scenario != null && !ReferenceEquals(choice.Scenario, scenario)) continue;
                        renderer.RenderPreview(PreviewRender.FromScenario(feature, scenario));
                    }
                    continue;
                }

                if (!warm)
                {
                    var failure = await runner.StartWarmSuite();
                    if (failure != null)
                    {
                        renderer.RenderCatastrophicFailure(failure);
                        input.ExitCode = 2;
                        return true;
                    }
                    warm = true;
                }

                var results = await runner.RunWarmSelection(input.FeatureFlag, input.TagFlag, choice.Matches);
                runner.RenderSummary(results);

                // The session's verdict is the LAST run's — the one the user left on.
                exitCode = results.ExitCode;
            }
        }
        finally
        {
            if (warm) await runner.StopWarmSuite();
        }

        input.ExitCode = exitCode;
        return true;
    }

    /// <summary>
    /// The TTY gate, pure and testable: the refusal message when the console cannot host a
    /// prompt, or null when interaction is possible.
    /// </summary>
    internal static string? DescribeNonInteractiveConsole(
        bool inputRedirected, bool outputRedirected, bool profileIsInteractive)
    {
        if (!inputRedirected && !outputRedirected && profileIsInteractive) return null;

        var reason = inputRedirected ? "standard input is redirected"
            : outputRedirected ? "standard output is redirected"
            : "the terminal profile is not interactive";

        return $"The interactive command needs a terminal, but {reason} — an interactive prompt " +
               "here would hang forever. Use 'list' to see the scenarios, 'preview' to inspect " +
               "them, or 'run --feature <name>' to run a subset.";
    }
}
