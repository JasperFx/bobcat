using Spectre.Console;

namespace Bobcat.Rendering;

/// <summary>
/// One scenario as plain text — the same rendering the console produces, through a colourless
/// console, for a destination that shows text and not escape codes (issues #409, #445).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is its own type.</b> <see cref="SpecOutput"/> hands a projected spec's scenario to
/// its runner's per-test output through it, and the console rendering it mirrors lives elsewhere,
/// so the plain-text answer lives in one place — the discipline <c>CellResult.derive()</c> and
/// <see cref="Bobcat.Engine.ScenarioReportVisibility"/> already follow.
/// </para>
/// <para>
/// <b>Plain text, not markup.</b> A test runner's output pane and a CI log show the text and none
/// of the escape codes, and the ✓/✗ glyphs survive on their own. The disagreeing row of a grid is
/// marked in text by <see cref="TextGrid"/> rather than coloured, so it is findable by eye in a CI
/// log and by <c>grep</c> — neither of which sees an ANSI code.
/// </para>
/// <para>
/// <b>Rendering can never fail the run.</b> Every entry point returns nothing rather than
/// throwing: a specification that reported something must not go red because the channel it
/// reported through did. The monitor publisher's invariant, and <see cref="SpecOutput"/>'s.
/// </para>
/// </remarks>
public static class PlainSpecText
{
    /// <summary>
    /// How wide the rendering is. Wide deliberately: a test pane wraps on its own, and a hard wrap
    /// here splits a step from its timing.
    /// </summary>
    public const int Width = 240;

    /// <summary>
    /// The whole scenario — feature header, steps, verdict — followed by its reports. Empty when
    /// the rendering throws, so a caller writes nothing rather than half a scenario.
    /// </summary>
    public static IReadOnlyList<string> Lines(SpecRender spec)
    {
        var scenario = ScenarioLines(spec);
        if (scenario.Count == 0) return scenario;

        var lines = new List<string>(scenario);
        lines.AddRange(ReportLines(spec));
        return lines;
    }

    /// <summary>
    /// The scenario without its reports. Empty when the rendering throws.
    /// </summary>
    public static IReadOnlyList<string> ScenarioLines(SpecRender spec)
    {
        string text;
        try
        {
            var writer = new StringWriter();
            var console = Console(writer);

            // Reports go after, through TextGrid: a fixed-width grid with the disagreeing row
            // marked in text is what a CI log and grep can read.
            var renderer = new CommandLineRenderer(console) { IncludeReports = false };
            if (spec.FeatureTitle is { Length: > 0 } feature) renderer.RenderFeatureHeader(feature);
            renderer.Render(spec);

            text = writer.ToString();
        }
        catch
        {
            return [];
        }

        return text.TrimEnd().Split('\n').Select(line => line.TrimEnd('\r')).ToList();
    }

    /// <summary>
    /// The scenario's reports only, each preceded by a blank line.
    /// <see cref="SpecRender.Reports"/> has already applied <see cref="Bobcat.Engine.ScenarioReportVisibility"/>
    /// and already knows whether the scenario failed, so this cannot reach a different answer than
    /// the console and the JSON report did.
    /// </summary>
    public static IReadOnlyList<string> ReportLines(SpecRender spec)
    {
        var lines = new List<string>();

        try
        {
            foreach (var report in spec.Reports)
            {
                lines.Add("");
                lines.AddRange(TextGrid.Render(report));
            }
        }
        catch
        {
            return [];
        }

        return lines;
    }

    /// <summary>
    /// The scenario's <see cref="SpecLog"/> lines alone (bobcat#547) — each step that logged, then
    /// the lines written between steps — for a runner output that is not given the whole scenario.
    /// <see cref="SpecRender"/> has already latched them, so this is empty for a passing scenario
    /// unless the run is verbose.
    /// </summary>
    public static IReadOnlyList<string> LogLines(SpecRender spec)
    {
        var lines = new List<string>();

        try
        {
            foreach (var step in spec.Steps.Where(x => x.Logs.Count > 0 || x.SuppressedLogs > 0))
            {
                lines.Add("");
                lines.Add($"Logs from: {(step.Keyword is { Length: > 0 } keyword ? keyword + " " : "")}{step.StepText}");
                addLogs(lines, step.Logs, step.SuppressedLogs);
            }

            if (spec.Logs.Count > 0 || spec.SuppressedLogs > 0)
            {
                lines.Add("");
                lines.Add("Logs:");
                addLogs(lines, spec.Logs, spec.SuppressedLogs);
            }
        }
        catch
        {
            return [];
        }

        return lines;
    }

    private static void addLogs(List<string> lines, IReadOnlyList<string> logs, int suppressed)
    {
        foreach (var log in logs)
        {
            lines.AddRange(log.Split('\n').Select(x => "  " + x.TrimEnd('\r')));
        }

        if (suppressed > 0) lines.Add("  " + Engine.StepLog.MoreLine(suppressed));
    }

    /// <summary>
    /// The scenario and its reports as one block, or an empty string when there is nothing to say.
    /// </summary>
    public static string Render(SpecRender spec)
    {
        var lines = Lines(spec);
        return lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// A console that emits text and nothing else.
    /// </summary>
    /// <remarks>
    /// Spectre's CI enrichers (GitHub Actions among them) turn ANSI back on after the settings
    /// above, which put escape codes into every test's output on CI — hence
    /// <c>UseDefaultEnrichers = false</c>.
    /// </remarks>
    private static IAnsiConsole Console(TextWriter writer)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });

        console.Profile.Width = Width;
        return console;
    }
}
