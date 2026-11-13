using System.Diagnostics;

namespace Bobcat.Supervisor;

/// <summary>
/// Runs a <see cref="GateDeclaration"/>: preflight every track a unit names, build once, print the
/// plan, run one <see cref="Supervisor"/> per unit under the track and budget rules, record each
/// unit's duration for next time, and report (bobcat#729).
/// </summary>
/// <remarks>
/// <para>
/// <b>Report, don't act</b> beyond scheduling. The gate's exit code is the worst of its units in
/// the supervisor's own convention — 2 over 1 over 0 — a pass-on-retry is reported as flaky and
/// never folded into clean, and nothing is retried that the project's own policy would not retry.
/// Every unit runs: a red unit does not cancel the rest, because a full report is the point of a
/// gate.
/// </para>
/// <para>
/// <b>Each unit is its own run on the monitor</b>, published under
/// <c>{RunTagPrefix}/{project}.{framework}</c> through <see cref="Supervisor.RunTag"/> (bobcat#768),
/// so a board shows one card per unit under the plan node that asked for the gate.
/// </para>
/// </remarks>
public sealed class Gate
{
    private readonly GateDeclaration _declaration;
    private readonly object _log = new();

    // Heals so far per track, across the whole gate run (bobcat#824): the service is the track's,
    // and the state that degrades it piles up across every unit that ran on it.
    private readonly Dictionary<Track, int> _heals = new();

    public Gate(GateDeclaration declaration) => _declaration = declaration;

    public async Task<GateResults> Run(CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var budget = Math.Max(1, _declaration.WorkerBudget);

        var units = _declaration.Units();
        var durations = _declaration.DurationsPath is { } durationsPath
            ? GateDurations.Load(durationsPath)
            : GateDurations.Empty();
        var plan = GateScheduler.Order(units, durations.Predict);

        // 1. Preflight, by track, before anything is built or launched: a stopped container is
        //    reported in seconds and by name, not ten minutes in as a thousand identical failures.
        var preflight = await runPreflight(units, ct);
        if (preflight.Any(p => !p.Results.Succeeded()))
        {
            var failed = preflight.Where(p => !p.Results.Succeeded()).Select(p => p.Track.Name);
            return finish(plan, [], preflight, budget, clock,
                $"preflight failed for track(s) {string.Join(", ", failed)}; nothing ran");
        }

        // 2. Build once. The hosts then run as built, and the bootstrap is paid once per gate
        //    rather than once per suite. Whoever built them, the next check is what says a
        //    selected framework was not produced.
        try
        {
            await build(units, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return finish(plan, [], preflight, budget, clock, $"the build failed: {e.Message}");
        }

        var missing = units.Where(u => !File.Exists(u.Executable)).ToArray();
        if (missing.Length > 0)
        {
            return finish(plan, [], preflight, budget, clock,
                "no test host at " + string.Join("; ", missing.Select(u => $"{u.Executable} ({u.Name})")) +
                " — a project built without UseMicrosoftTestingPlatformRunner yields no supervisable host at that path");
        }

        // 3. The plan, printed before anything runs, so predicted can be set against actual after.
        log($"Gate: {units.Count} unit(s), {budget} worker(s) budgeted, frameworks {_declaration.Frameworks}" +
            (_declaration.RunTagPrefix is { } prefix ? $", publishing under {prefix}/<unit>" : ""));
        var width = plan.Max(u => u.Name.Length);
        for (var i = 0; i < plan.Count; i++)
        {
            var unit = plan[i];
            log($"  {i + 1,3}. {unit.Name.PadRight(width)}  {GateScheduler.WorkersFor(unit, budget)} worker(s)" +
                $"  tracks: {(unit.Tracks.Count == 0 ? "(none)" : string.Join(", ", unit.Tracks.Select(t => t.Name)))}" +
                $"  predicted: {(durations.Predict(unit) is { } p ? GateResults.Humanize(p) : "first run")}");
        }

        // 4. Run, longest first, never two on a track, never past the budget.
        var results = new List<GateUnitResult>();
        await GateScheduler.Run(plan, budget, async (unit, workers, token) =>
        {
            var result = await runUnit(unit, workers, durations.Predict(unit), token);
            lock (results) results.Add(result);
        }, ct);

        // 5. Remember what each unit took, for next time's order.
        if (_declaration.DurationsPath is { } path)
        {
            foreach (var result in results.Where(r => r.Results is not null)) durations.Record(result.Unit.Name, result.Duration);

            try
            {
                durations.Save(path);
            }
            catch (Exception e)
            {
                log($"Could not write the gate's duration record to {path}: {e.Message}");
            }
        }

        return finish(plan, results, preflight, budget, clock, abortReason: null);
    }

    private GateResults finish(
        IReadOnlyList<GateUnit> plan,
        IReadOnlyList<GateUnitResult> ran,
        IReadOnlyList<TrackPreflight> preflight,
        int budget,
        Stopwatch clock,
        string? abortReason)
    {
        var started = ran.ToDictionary(r => r.Unit.Name, StringComparer.Ordinal);
        var all = ran.ToList();
        all.AddRange(plan.Where(u => !started.ContainsKey(u.Name))
            .Select(u => new GateUnitResult(u, null, TimeSpan.Zero, null, 0, u.RunTagUnder(_declaration.RunTagPrefix))));

        var results = new GateResults
        {
            Units = all,
            Preflight = preflight,
            Plan = plan,
            AbortReason = abortReason,
            WorkerBudget = budget,
            Duration = clock.Elapsed
        };

        log(results.ToText());
        return results;
    }

    private async Task<IReadOnlyList<TrackPreflight>> runPreflight(IReadOnlyList<GateUnit> units, CancellationToken ct)
    {
        var used = units.SelectMany(u => u.Tracks).Distinct().ToHashSet();
        var results = new List<TrackPreflight>();

        foreach (var track in _declaration.Tracks.Where(used.Contains))
        {
            if (track.Preflight.IsEmpty) continue;

            var outcome = await track.Preflight.Run(ct);
            results.Add(new TrackPreflight(track, outcome));

            if (!outcome.Succeeded())
            {
                log($"✗ track {track.Name} failed preflight:");
                log(Bobcat.Runtime.Preflight.Describe(outcome));
            }
        }

        return results;
    }

    private async Task build(IReadOnlyList<GateUnit> units, CancellationToken ct)
    {
        if (_declaration.SkipBuild) return;

        var frameworks = units.Select(u => u.Framework)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(f => f, TargetFrameworks.Comparer)
            .ToArray();

        var request = new GateBuild(_declaration.Solution, _declaration.Projects, frameworks, _declaration.Configuration);

        if (_declaration.Build is { } custom)
        {
            await custom(request, ct);
            return;
        }

        foreach (var step in DefaultBuildSteps(request.Solution, units))
        {
            await dotnetBuild(step, ct);
        }
    }

    /// <summary>
    /// The default build, as the <c>dotnet build</c> invocations it makes (bobcat#774).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A solution is built once, with no <c>-f</c>.</b> <c>-f</c> on a solution applies to every
    /// project in it, so a netstandard2.0 source generator or a net9.0-only library beside the
    /// test projects fails to build and the gate never reaches its units. Without it each project
    /// builds its own <c>TargetFrameworks</c>, and the check that every selected unit's host exists
    /// — which runs next whatever built them — is what says a framework was not produced.
    /// </para>
    /// <para>
    /// <b>Without a solution each unit's project is built for that unit's framework</b>, once per
    /// (project, framework). A framework is only ever a unit's when its project declares it, and
    /// <c>-f</c> on one project does not reach its references (each negotiates its nearest
    /// framework), so this is safe and builds no framework the gate will not run.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<GateBuildStep> DefaultBuildSteps(string? solution, IReadOnlyList<GateUnit> units)
    {
        if (solution is not null) return [new GateBuildStep(solution, null)];

        return units
            .Select(u => new GateBuildStep(u.Project.Path, u.Framework))
            .Distinct()
            .ToArray();
    }

    /// <summary>The arguments of one default <c>dotnet build</c>.</summary>
    internal static string BuildArguments(GateBuildStep step, string configuration)
        => $"build \"{step.Target}\" -c {configuration}" +
           (step.Framework is { } framework ? $" -f {framework}" : "") +
           " --nologo -v quiet";

    private async Task dotnetBuild(GateBuildStep step, CancellationToken ct)
    {
        var arguments = BuildArguments(step, _declaration.Configuration);
        log($"dotnet {arguments}");

        var info = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // One build at a time, and never a lingering node that outlives the gate.
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = Process.Start(info) ?? throw new InvalidOperationException("dotnet could not be started.");

        var tail = new Queue<string>();
        void keep(string? line)
        {
            if (line is null) return;
            log($"  {line}");
            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > 20) tail.Dequeue();
            }
        }

        process.OutputDataReceived += (_, e) => keep(e.Data);
        process.ErrorDataReceived += (_, e) => keep(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        if (process.ExitCode != 0)
        {
            string[] lines;
            lock (tail) lines = tail.ToArray();
            throw new InvalidOperationException(
                $"dotnet build {Path.GetFileName(step.Target)}{(step.Framework is { } f ? $" -f {f}" : "")} exited {process.ExitCode}:{Environment.NewLine}" +
                string.Join(Environment.NewLine, lines));
        }
    }

    private async Task<GateUnitResult> runUnit(GateUnit unit, int workers, TimeSpan? predicted, CancellationToken ct)
    {
        var tag = unit.RunTagUnder(_declaration.RunTagPrefix);
        var clock = Stopwatch.StartNew();
        log($"=== {unit.Name}: starting on {workers} worker(s)" +
            (unit.Tracks.Count > 0 ? $", holding {string.Join(", ", unit.Tracks.Select(t => t.Name))}" : "") + " ===");

        try
        {
            var supervisor = supervisorFor(unit, workers, tag);
            var results = await supervisor.Run(ct);
            var result = new GateUnitResult(unit, results, clock.Elapsed, predicted, workers, tag);
            log($"=== {unit.Name}: {result.Mark} {result.Summary} in {GateResults.Humanize(result.Duration)} ===");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // The supervisor itself threw — a host that would not start, a factory that failed.
            // The unit is Indeterminate with the exception, and the rest of the gate runs.
            log($"=== {unit.Name}: ? {e.GetType().Name}: {e.Message} ===");
            return new GateUnitResult(unit, null, clock.Elapsed, predicted, workers, tag, e);
        }
    }

    private Supervisor supervisorFor(GateUnit unit, int workers, string? tag)
    {
        var factory = (unit.Project.WorkerFactory ?? _declaration.WorkerFactory)?.Invoke(unit)
                      ?? new MtpWorkerFactory(unit.Executable);

        var supervisor = new Supervisor(factory)
        {
            MaxParallelWorkers = workers,
            PublishToMonitor = _declaration.PublishToMonitor,
            MonitorSink = _declaration.MonitorSink,
            RunTag = tag,
            Log = message => log($"[{unit.Name}] {message}")
        };

        // The gate owns the tracks, so the gate heals (bobcat#824): a unit on a track that declared
        // a heal gets the supervisor's degradation handler; any other unit only reports. Set before
        // the project's own configuration, which may replace it.
        if (unit.Tracks.Any(t => t.CanHeal))
        {
            supervisor.OnDegraded = (degradation, ct) => healTracks(unit, degradation, ct);
        }

        _declaration.Configure?.Invoke(unit, supervisor);
        unit.Project.Configure?.Invoke(unit, supervisor);

        // The budget is the gate's: a project may run on fewer lanes than it was charged for,
        // never more.
        if (supervisor.MaxParallelWorkers > workers)
        {
            log($"[{unit.Name}] {supervisor.MaxParallelWorkers} workers asked for, {workers} allowed by the budget");
            supervisor.MaxParallelWorkers = workers;
        }

        return supervisor;
    }

    /// <summary>
    /// Heal every track of <paramref name="unit"/> that declared a heal, in declaration order
    /// (bobcat#824), while the unit's supervisor holds the run paused with nothing in flight.
    /// Answers the tracks healed, or the reason to abort the unit: a track past its
    /// <see cref="Track.MaxHeals"/>, a heal that threw, or a preflight that failed after the heal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The budget is checked for every track before any is healed</b>, so a unit holding two
    /// healable tracks is never half-healed and then aborted. Other units keep running: the abort
    /// is the unit's <see cref="SupervisorResults.AbortReason"/>, reported as the gate reports any
    /// aborted unit, and a unit on another track is not touched.
    /// </para>
    /// <para>
    /// <b>The preflight re-runs after the heal</b> because a recreated service that does not answer
    /// is the one state worse than a degraded one: every resumed test would fail instantly. The same
    /// checks that gated the gate's start gate the resume, so a heal need not repeat them.
    /// </para>
    /// </remarks>
    private async Task<DegradationResponse> healTracks(GateUnit unit, RunDegradation degradation, CancellationToken ct)
    {
        var healable = unit.Tracks.Where(t => t.CanHeal).ToList();

        List<(Track Track, int Heal)> planned = [];
        lock (_heals)
        {
            foreach (var track in healable)
            {
                _heals.TryGetValue(track, out var healed);
                if (healed >= track.MaxHeals)
                {
                    return DegradationResponse.Abort(
                        $"track {track.Name} degraded again after being healed {healed} time(s) this gate run " +
                        $"(MaxHeals = {track.MaxHeals}), so the unit is aborted rather than recreating its service " +
                        $"a {ordinal(healed + 1)} time: {degradation.Reason}");
                }

                planned.Add((track, healed + 1));
            }
        }

        var healedTracks = new List<HealedTrack>();
        foreach (var (track, number) in planned)
        {
            log($"[{unit.Name}] healing track {track.Name} (heal {number} of {track.MaxHeals}): {degradation.Reason}");

            try
            {
                await track.Healer!(new TrackHealContext(track, unit, degradation, number), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                return DegradationResponse.Abort(
                    $"healing track {track.Name} failed ({e.GetType().Name}: {e.Message}), so the unit is aborted " +
                    $"rather than resumed against a service in an unknown state: {degradation.Reason}");
            }

            // Counted once the heal ran, whether or not the preflight then passes: the service was
            // recreated, and a second degradation after a failed preflight is not a free heal.
            lock (_heals) _heals[track] = number;

            if (!track.Preflight.IsEmpty)
            {
                var outcome = await track.Preflight.Run(ct);
                if (!outcome.Succeeded())
                {
                    var described = Bobcat.Runtime.Preflight.Describe(outcome);
                    log($"[{unit.Name}] ✗ track {track.Name} failed preflight after being healed:");
                    log(described);
                    return DegradationResponse.Abort(
                        $"track {track.Name} failed its preflight after being healed (heal {number}), so the unit is " +
                        $"aborted rather than resumed against a service that does not answer: {described.Trim()}");
                }
            }

            log($"[{unit.Name}] track {track.Name} healed and its preflight passed");
            healedTracks.Add(new HealedTrack(track.Name, number));
        }

        return DegradationResponse.HealedTracks(healedTracks);
    }

    private static string ordinal(int n) => n switch
    {
        1 => "first",
        2 => "second",
        3 => "third",
        _ => $"{n}th"
    };

    private void log(string message)
    {
        lock (_log) _declaration.Log(message);
    }
}

/// <summary>One default <c>dotnet build</c>: a solution or project, and the framework, or null for its own.</summary>
internal sealed record GateBuildStep(string Target, string? Framework);
