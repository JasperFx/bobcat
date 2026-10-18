using Bobcat.Engine;
using Bobcat.Rendering;
using Spectre.Console;

namespace Bobcat;

/// <summary>
/// Renders the specifications a projected test run produced to the console, grouped by feature —
/// Spectre output for a suite of ordinary xUnit or TUnit tests.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Everything a projected test recorded used to leave the process only over
/// the monitor wire, so the specification a suite produces could not be read without a console
/// running somewhere. That made the rendering — the whole point of projecting tests in the first
/// place — the one thing a consumer could not see, and the one thing nobody could review.
/// </para>
/// <para>
/// <b>On by default when a terminal is attached and nothing is listening on the wire</b> — which is
/// precisely the case where the run would otherwise say nothing at all (issue #384). It was opt-in
/// first, and that was the wrong default for a lane people adopt one class at a time: the first thing
/// an author does after decorating a helper is look at what it renders, and there was nowhere to look
/// that did not involve knowing an environment variable existed.
/// </para>
/// <para>
/// The two conditions are what keep it from being a nuisance. <b>A terminal</b>, because a captured
/// stream belongs to whoever captured it — under <c>dotnet test</c> or on a CI runner the platform
/// owns the output and a second report would interleave with it. <b>Nothing on the wire</b>, because
/// a console already renders these specifications far better than Spectre can, and printing them
/// twice reads as two reports of one run.
/// </para>
/// <para>
/// <c>BOBCAT_SPEC_CONSOLE</c> overrides the default <b>in both directions</b>: <c>1</c> prints even
/// into a captured stream with a console listening, and <c>0</c> stays silent even in a terminal with
/// nothing listening. An unset variable is the third state, and the only one the default decides.
/// </para>
/// <para>
/// <b>Written at process exit, not after the last test.</b> Nothing here knows which test is the
/// last one — the runner does, and it does not say. The same reason
/// <see cref="MarkerStepRun"/> closes its run bracket there.
/// </para>
/// <para>
/// <b>A redirected stdout is written through a stream this class opens itself (bobcat#676).</b> By
/// process exit, <see cref="Console.Out"/> — and the <see cref="AnsiConsole.Console"/> singleton,
/// which binds to whatever <see cref="Console.Out"/> was the first time anything touched it — may
/// be a writer some test, logger or test platform swapped in and has since closed or stopped
/// flushing. In a terminal that cost nothing visible, which is why it read as "works locally"; with
/// stdout captured (CI, the supervisor, a pipe) the whole report was lost without a word. A
/// failure to write is reported on standard error rather than swallowed, and
/// <see cref="OutputFileEnvironmentVariable"/> stays the route that depends on no stream at all.
/// </para>
/// </remarks>
public static class ProjectedSpecConsole
{
    /// <summary>
    /// Set this to <c>1</c>/<c>true</c> to have a projected run print its specs, or <c>0</c>/
    /// <c>false</c> to stay silent. Unset leaves the decision to <see cref="WouldEnableByDefault"/>.
    /// </summary>
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

    /// <summary>
    /// A file the exit-time rendering is written to instead of standard output — how the
    /// <c>bobcat</c> tool keeps a test host's own logging out of the specifications. Written with
    /// colour, at <see cref="WidthEnvironmentVariable"/> columns, for the tool to replay verbatim.
    /// </summary>
    public const string OutputFileEnvironmentVariable = "BOBCAT_SPEC_CONSOLE_FILE";

    /// <summary>The width the rendering is laid out at when it goes to <see cref="OutputFileEnvironmentVariable"/>.</summary>
    public const string WidthEnvironmentVariable = "BOBCAT_SPEC_CONSOLE_WIDTH";

    /// <summary>
    /// Where the rendering goes: the file the tool asked for, or the process console. The file is
    /// appended to, so a preview and a run in one process both land in it.
    /// </summary>
    private static (IAnsiConsole Console, IDisposable? Owner) target()
    {
        var path = Environment.GetEnvironmentVariable(OutputFileEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return OutputRedirected() ? standardOutput() : (AnsiConsole.Console, null);
        }

        var writer = new StreamWriter(path, append: true) { AutoFlush = true };
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.Standard,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer)
        });

        console.Profile.Width =
            int.TryParse(Environment.GetEnvironmentVariable(WidthEnvironmentVariable), out var width) && width > 20
                ? width
                : 140;

        return (console, writer);
    }

    /// <summary>Test seam: whether standard output is captured rather than a terminal.</summary>
    internal static Func<bool> OutputRedirected = () => Console.IsOutputRedirected;

    /// <summary>Test seam: the process's standard output, as a raw stream.</summary>
    internal static Func<Stream> OpenStandardOutput = Console.OpenStandardOutput;

    /// <summary>
    /// A console over the raw standard output stream rather than <see cref="Console.Out"/>, so the
    /// report does not depend on which writer the process holds at exit (bobcat#676). Laid out as a
    /// captured stream always was — plain text (coloured only where Spectre's CI enrichers say so), Spectre's default width — unless
    /// <see cref="WidthEnvironmentVariable"/> says otherwise.
    /// </summary>
    private static (IAnsiConsole Console, IDisposable? Owner) standardOutput()
    {
        // Whatever the process already wrote through Console.Out goes first, so the report still
        // follows the test platform's summary rather than overtaking it.
        try
        {
            Console.Out.Flush();
        }
        catch
        {
            // A closed writer is exactly the case this route exists for.
        }

        // leaveOpen: the stream is the process's standard output, which is not ours to close.
        var writer = new StreamWriter(OpenStandardOutput(), new System.Text.UTF8Encoding(false), 4096, leaveOpen: true);
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            // Not detected: for a writer that is not Console.Out Spectre falls back to TERM and would
            // colour a pipe or a file. Its enrichers still turn colour on where the capture renders it
            // (GitHub Actions), as they did for the console's own captured output.
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer)
        });

        if (int.TryParse(Environment.GetEnvironmentVariable(WidthEnvironmentVariable), out var width) && width > 20)
        {
            console.Profile.Width = width;
        }

        return (console, writer);
    }

    private static readonly object _gate = new();
    private static readonly List<SpecRender> _specs = new();

    // The timing snapshot of each captured scenario (issue #466), not the recording itself: a
    // recording holds exceptions and reports, and this list lives until process exit.
    private static readonly List<Runtime.ProjectedScenarioSample> _timings = new();
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
                AppDomain.CurrentDomain.ProcessExit += (_, _) => atExit(Render);
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
            _timings.Clear();
        }
    }

    /// <summary>
    /// Switch on from <see cref="EnvironmentVariable"/>. Called as a projected run opens its
    /// bracket, so a consumer needs no code at all — one environment variable and the specs print.
    /// </summary>
    public static void EnableIfRequested()
    {
        if (Setting(PreviewEnvironmentVariable) == true) EnablePreview();
        if (Setting(EnvironmentVariable) == true) Enable();

        // Naming a file to render into is a request to render.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OutputFileEnvironmentVariable))
            && Setting(EnvironmentVariable) != false) Enable();
    }

    /// <summary>
    /// Switch on unless something says otherwise — called once the run knows whether a console
    /// answered, which <see cref="EnableIfRequested"/> cannot: it runs first, deliberately, so an
    /// explicitly requested report survives a wire probe that hangs.
    /// </summary>
    /// <param name="wireIsLive">Whether a monitor answered the publisher's probe.</param>
    public static void EnableByDefault(bool wireIsLive)
    {
        if (WouldEnableByDefault(Setting(EnvironmentVariable), TerminalAttached, wireIsLive)) Enable();
    }

    /// <summary>
    /// Whether a projected run prints its specifications, given the three facts that decide it. Pure,
    /// so the rule can be read and tested without a process, a terminal or a console.
    /// </summary>
    /// <param name="setting">
    /// <c>BOBCAT_SPEC_CONSOLE</c> as a tri-state: true, false, or null for unset. An explicit setting
    /// always wins — the default is only consulted when nobody said.
    /// </param>
    public static bool WouldEnableByDefault(bool? setting, bool terminalAttached, bool wireIsLive)
        => setting ?? (terminalAttached && !wireIsLive);

    /// <summary>
    /// Whether standard output is a terminal rather than a captured stream. A redirected stream
    /// belongs to whoever redirected it — a test platform, a CI runner, a pipe — and is the one case
    /// where an unasked-for second report is in the way rather than useful.
    /// </summary>
    public static bool TerminalAttached => !Console.IsOutputRedirected;

    /// <summary>
    /// A <c>BOBCAT_*</c> switch as a tri-state: null when unset, so "nobody said" stays
    /// distinguishable from "somebody said no".
    /// </summary>
    public static bool? Setting(string variable)
    {
        var setting = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(setting)) return null;

        return setting.Trim() is not ("0" or "false" or "False" or "FALSE");
    }

    /// <summary>Print the preview at process exit. Independent of result capture — either, or both.</summary>
    public static void EnablePreview()
    {
        lock (_gate)
        {
            if (_previewAtExit) return;
            _previewAtExit = true;

            AppDomain.CurrentDomain.ProcessExit += (_, _) => atExit(RenderPreview);
        }
    }

    /// <summary>
    /// Print every projected specification as its plan, without results — feature by feature.
    /// </summary>
    public static void RenderPreview()
    {
        var specs = ProjectedSpecPreview.All();
        if (specs.Count == 0) return;

        var (console, owner) = target();
        using var _ = owner;
        var renderer = new CommandLineRenderer(console);

        foreach (var feature in specs.GroupBy(x => x.FeatureTitle ?? "").OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            renderer.RenderFeatureHeader(feature.Key);

            foreach (var spec in feature) renderer.RenderPreview(spec);

            console.WriteLine();
        }

        console.MarkupLine($"[bold]{specs.Count} specification(s) previewed[/]");
        console.WriteLine();
    }

    /// <summary>
    /// Print every captured specification, grouped by feature, followed by a one-line total — then
    /// clear, so calling twice cannot print a run twice.
    /// </summary>
    public static void Render()
    {
        List<SpecRender> specs;
        List<Runtime.ProjectedScenarioSample> timings;
        lock (_gate)
        {
            if (_specs.Count == 0) return;
            specs = _specs.ToList();
            _specs.Clear();
            timings = _timings.ToList();
            _timings.Clear();
        }

        var (console, owner) = target();
        using var _ = owner;
        var renderer = new CommandLineRenderer(console);
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

                // A failing Acceptance spec's counts are on its own verdict line; adding its wrongs
                // to a total printed as "Succeeded" would be the contradiction RenderCounts avoids.
                if (spec.AcceptanceFailing) continue;

                total.Rights += spec.Counts.Rights;
                total.Wrongs += spec.Counts.Wrongs;
                total.Errors += spec.Counts.Errors;
            }
        }

        // A failing Acceptance spec (bobcat#545) does not turn the run red; it is counted on its own.
        var green = specs.All(x => !x.FailsTheRun);
        var acceptanceFailing = specs.Count(x => x.AcceptanceFailing);
        var readyToPromote = specs.Count(x => x.ReadyToPromote);
        var verdict = green
            ? acceptanceFailing > 0 ? "[green]green[/]" : "[green]all green[/]"
            : "[red]not green[/]";
        var acceptance = acceptanceFailing > 0 ? $", [yellow]{acceptanceFailing} {SpecLifecycle.FailingVerdict}[/]" : "";
        var promote = readyToPromote > 0 ? $", [cyan]{readyToPromote} acceptance spec(s) {SpecLifecycle.ReadyToPromote}[/]" : "";
        console.MarkupLine($"[bold]{specs.Count} specification(s)[/] — {verdict}{acceptance}{promote}");
        renderer.RenderCounts(total, green);

        // Where the time went (issues #142, #466) -- from the projected recordings, with the lifecycle reported as not measured rather than as zero.
        renderer.RenderTimingSummary(Runtime.SuiteTiming.ForProjected(timings));
        console.WriteLine();
    }

    /// <summary>
    /// Run an exit-time write so that a failure says so on standard error. A throw out of a
    /// <c>ProcessExit</c> handler loses the report with nothing to show why, which is how bobcat#676
    /// went unexplained.
    /// </summary>
    private static void atExit(Action write)
    {
        try
        {
            write();
        }
        catch (Exception e)
        {
            try
            {
                Console.Error.WriteLine(
                    $"Bobcat could not write the specification report at process exit: {e.GetType().Name}: {e.Message}. "
                    + $"Set {OutputFileEnvironmentVariable} to write it to a file instead.");
            }
            catch
            {
                // Nowhere left to say it.
            }
        }
    }

    private static void capture(ScenarioRecorder.Recording recording)
    {
        var spec = SpecRender.FromRecording(recording);
        var timing = Runtime.ProjectedScenarioSample.From(recording);
        lock (_gate)
        {
            _specs.Add(spec);
            _timings.Add(timing);
        }
    }
}
