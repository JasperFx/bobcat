using Bobcat.Engine;
using Bobcat.Rendering;
using Spectre.Console;

namespace Bobcat;

/// <summary>
/// Renders the specifications a projected test run produced to the console, grouped by feature —
/// the Spectre output a <c>.feature</c> suite gets, for a suite of ordinary xUnit or TUnit tests.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Everything a projected test recorded used to leave the process only over
/// the monitor wire, so the specification a suite produces could not be read without a console
/// running somewhere. That made the rendering — the whole point of projecting tests in the first
/// place — the one thing a consumer could not see, and the one thing nobody could review.
/// </para>
/// <para>
/// <b>Opt-in, and silent otherwise.</b> A test runner's output belongs to the runner; a suite that
/// did not ask for a second report must not get one. Set <c>BOBCAT_SPEC_CONSOLE=1</c>, or call
/// <see cref="Enable"/>.
/// </para>
/// <para>
/// <b>Written at process exit, not after the last test.</b> Nothing here knows which test is the
/// last one — the runner does, and it does not say. The same reason
/// <see cref="MarkerStepRun"/> closes its run bracket there.
/// </para>
/// </remarks>
public static class ProjectedSpecConsole
{
    /// <summary>Set this to <c>1</c> or <c>true</c> to have a projected run print its specs.</summary>
    public const string EnvironmentVariable = "BOBCAT_SPEC_CONSOLE";

    /// <summary>
    /// Set this to <c>1</c> or <c>true</c> to print every projected specification WITHOUT its
    /// results — the projected lane's <c>bobcat preview</c>.
    /// </summary>
    /// <remarks>
    /// Pair it with the test platform's own <c>--list-tests</c> to preview without executing
    /// anything at all: the plan is registered by a module initializer, so it is known before a
    /// single test runs — which is the same rule that keeps MTP discovery from starting resources.
    /// </remarks>
    public const string PreviewEnvironmentVariable = "BOBCAT_SPEC_PREVIEW";

    private static readonly object _gate = new();
    private static readonly List<SpecRender> _specs = new();
    private static bool _enabled;
    private static bool _atExitRegistered;
    private static bool _previewAtExit;

    /// <summary>Whether the console report is switched on for this process.</summary>
    public static bool IsEnabled
    {
        get { lock (_gate) return _enabled; }
    }

    /// <summary>
    /// Every specification captured so far, in the order the scenarios finished. Exposed so a
    /// consumer can render or assert on them itself rather than only through this class.
    /// </summary>
    public static IReadOnlyList<SpecRender> Captured
    {
        get { lock (_gate) return _specs.ToList(); }
    }

    /// <summary>
    /// Start capturing, and print at process exit. Idempotent, so a module initializer and an
    /// environment variable cannot double-report.
    /// </summary>
    public static void Enable()
    {
        lock (_gate)
        {
            if (_enabled) return;
            _enabled = true;

            ScenarioRecorder.ScenarioCompleted += capture;

            if (!_atExitRegistered)
            {
                _atExitRegistered = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Render();
            }
        }
    }

    /// <summary>Stop capturing and forget what was captured. The test seam, and an escape hatch.</summary>
    public static void Disable()
    {
        lock (_gate)
        {
            if (!_enabled) return;
            _enabled = false;
            ScenarioRecorder.ScenarioCompleted -= capture;
            _specs.Clear();
        }
    }

    /// <summary>
    /// Switch on from <see cref="EnvironmentVariable"/>. Called as a projected run opens its
    /// bracket, so a consumer needs no code at all — one environment variable and the specs print.
    /// </summary>
    public static void EnableIfRequested()
    {
        if (requested(PreviewEnvironmentVariable)) EnablePreview();
        if (requested(EnvironmentVariable)) Enable();
    }

    private static bool requested(string variable)
    {
        var setting = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(setting)) return false;

        return setting.Trim() is not ("0" or "false" or "False" or "FALSE");
    }

    /// <summary>Print the preview at process exit. Independent of result capture — either, or both.</summary>
    public static void EnablePreview()
    {
        lock (_gate)
        {
            if (_previewAtExit) return;
            _previewAtExit = true;

            AppDomain.CurrentDomain.ProcessExit += (_, _) => RenderPreview();
        }
    }

    /// <summary>
    /// Print every projected specification as its plan, without results — feature by feature.
    /// </summary>
    public static void RenderPreview()
    {
        var specs = ProjectedSpecPreview.All();
        if (specs.Count == 0) return;

        var renderer = new CommandLineRenderer();

        foreach (var feature in specs.GroupBy(x => x.FeatureTitle ?? "").OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            renderer.RenderFeatureHeader(feature.Key);

            foreach (var spec in feature) renderer.RenderPreview(spec);

            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine($"[bold]{specs.Count} specification(s) previewed[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Print every captured specification, grouped by feature, followed by a one-line total — then
    /// clear, so calling twice cannot print a run twice.
    /// </summary>
    public static void Render()
    {
        List<SpecRender> specs;
        lock (_gate)
        {
            if (_specs.Count == 0) return;
            specs = _specs.ToList();
            _specs.Clear();
        }

        var renderer = new CommandLineRenderer();
        var total = new Counts();

        // Grouped by feature, and within a feature by TITLE.
        //
        // Not by the order the scenarios finished: a test runner is free to run tests in any order
        // and in parallel, so that order changes between runs of an unchanged suite — which makes
        // two reports impossible to diff and was the first thing to go wrong when this report was
        // read twice. Not by outcome either: shuffling the failures to the top would mean a
        // specification suite no longer reads as a document.
        //
        // Source order would be better still, and is not available: it is a compile-time fact the
        // generator knows and does not currently register.
        foreach (var feature in specs.GroupBy(x => x.FeatureTitle ?? "").OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            renderer.RenderFeatureHeader(feature.Key);

            foreach (var spec in feature.OrderBy(x => x.Title, StringComparer.Ordinal))
            {
                renderer.Render(spec);

                total.Rights += spec.Counts.Rights;
                total.Wrongs += spec.Counts.Wrongs;
                total.Errors += spec.Counts.Errors;
            }
        }

        var green = specs.All(x => x.Succeeded);
        AnsiConsole.MarkupLine(
            $"[bold]{specs.Count} specification(s)[/] — {(green ? "[green]all green[/]" : "[red]not green[/]")}");
        renderer.RenderCounts(total, green);
        AnsiConsole.WriteLine();
    }

    private static void capture(ScenarioRecorder.Recording recording)
    {
        var spec = SpecRender.FromRecording(recording);
        lock (_gate) _specs.Add(spec);
    }
}
